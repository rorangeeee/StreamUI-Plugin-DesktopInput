using System.Buffers.Binary;

namespace StreamUI.DesktopInput;

internal sealed class InputState
{
    internal const int FrameSize = 75;
    internal const int PauseCode = 0xc5;
    internal const int PrintScreenCode = 0xb7;
    private const int MouseStart = 256;
    private const int ScrollUp = MouseStart + 5;
    private const int ScrollDown = MouseStart + 6;
    private const int PulseMilliseconds = 80;

    private sealed class Device
    {
        internal readonly HashSet<int> Held = [];
        internal AbsolutePosition? Position;
    }

    internal readonly record struct AbsolutePosition(int X, int Y, int Width, int Height, bool VirtualDesktop);

    private readonly object gate = new();
    private readonly Dictionary<nint, Device> devices = [];
    private readonly int[] held = new int[MouseStart + 5];
    private readonly long[] pulseUntil = new long[MouseStart + 7];
    private double deltaX, deltaY;

    private Device GetDevice(nint handle)
    {
        if (devices.TryGetValue(handle, out var device)) return device;
        device = new();
        devices.Add(handle, device);
        return device;
    }

    private void SetButton(Device device, int code, bool down, long now)
    {
        if (down && device.Held.Add(code))
        {
            held[code]++;
            pulseUntil[code] = now + PulseMilliseconds;
        }
        else if (!down && device.Held.Remove(code)) held[code]--;
    }

    internal void Key(nint handle, int code, bool down, long now)
    {
        lock (gate)
        {
            var device = GetDevice(handle);
            // Pause has no ordinary break, and some Print Screen sequences send only a break.
            if (code == PauseCode || (code == PrintScreenCode && !down && !device.Held.Contains(code)))
            {
                if (down || code == PrintScreenCode) pulseUntil[code] = now + PulseMilliseconds;
                return;
            }
            SetButton(device, code, down, now);
        }
    }

    internal void Mouse(nint handle, ushort buttons, short wheel, int x, int y, AbsolutePosition? absolute, long now)
    {
        lock (gate)
        {
            var device = GetDevice(handle);
            for (var button = 0; buttons != 0 && button < 5; button++)
            {
                var downFlag = 1 << (button * 2);
                if ((buttons & downFlag) != 0) SetButton(device, MouseStart + button, true, now);
                if ((buttons & (downFlag << 1)) != 0) SetButton(device, MouseStart + button, false, now);
            }
            if ((buttons & 0x0400) != 0 && wheel != 0)
                pulseUntil[wheel > 0 ? ScrollUp : ScrollDown] = now + PulseMilliseconds;

            if (absolute is { } current)
            {
                if (device.Position is { } previous && previous.Width == current.Width && previous.Height == current.Height
                    && previous.VirtualDesktop == current.VirtualDesktop)
                {
                    deltaX += (current.X - (double)previous.X) * current.Width / 65535;
                    deltaY += (current.Y - (double)previous.Y) * current.Height / 65535;
                }
            }
            else
            {
                deltaX += x;
                deltaY += y;
            }
            device.Position = absolute;
        }
    }

    internal void RemoveDevice(nint handle)
    {
        lock (gate)
        {
            if (!devices.Remove(handle, out var device)) return;
            foreach (var code in device.Held)
                if (--held[code] == 0) pulseUntil[code] = 0;
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            devices.Clear();
            Array.Clear(held);
            Array.Clear(pulseUntil);
            deltaX = deltaY = 0;
        }
    }

    internal void WriteFrame(Span<byte> frame, long now, double seconds)
    {
        lock (gate)
        {
            frame.Clear();
            frame[0] = 1;
            for (var code = 0; code < MouseStart; code++)
            {
                var bit = (byte)(1 << (code % 8));
                if (held[code] > 0) frame[3 + code / 8] |= bit;
                if (held[code] > 0 || pulseUntil[code] > now) frame[35 + code / 8] |= bit;
            }
            for (var button = 0; button < 7; button++)
            {
                var isHeld = button < 5 && held[MouseStart + button] > 0;
                if (isHeld) frame[1] |= (byte)(1 << button);
                if (isHeld || pulseUntil[MouseStart + button] > now) frame[2] |= (byte)(1 << button);
            }
            BinaryPrimitives.WriteSingleLittleEndian(frame[67..], (float)(deltaX / seconds));
            BinaryPrimitives.WriteSingleLittleEndian(frame[71..], (float)(deltaY / seconds));
            deltaX = deltaY = 0;
        }
    }
}
