using System.Buffers.Binary;

namespace StreamUI.DesktopInput;

internal sealed class InputState
{
    internal const int FrameSize = 75;
    internal const int PauseCode = 0xc5;
    internal const int PrintScreenCode = 0xb7;
    internal const int MouseStart = 256;
    internal const int ScrollUp = MouseStart + 5;
    internal const int ScrollDown = MouseStart + 6;
    private const int PulseMilliseconds = 80;
    private const int HookMatchMilliseconds = 24;

    private sealed class Device
    {
        internal readonly HashSet<int> Held = [];
        internal AbsolutePosition? Position;
    }

    internal readonly record struct AbsolutePosition(int X, int Y, int Width, int Height, bool VirtualDesktop);
    private readonly record struct HookButton(int Code, bool Down, long At);

    private readonly object gate = new();
    private readonly Dictionary<nint, Device> devices = [];
    private readonly int[] held = new int[MouseStart + 5];
    private readonly long[] pulseUntil = new long[MouseStart + 7];
    private readonly Device hookDevice = new();
    private readonly Queue<HookButton> hookButtons = new();
    private readonly long[] rawDownAt = new long[MouseStart + 7];
    private readonly long[] rawUpAt = new long[MouseStart + 7];
    private (int X, int Y)? hookPosition;
    private long rawMotionAt = long.MinValue;
    private double deltaX, deltaY;
    private double hookDeltaX, hookDeltaY;

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
        lock (gate) RecordRawButton(GetDevice(handle), code, down, now);
    }

    private void ApplyButton(Device device, int code, bool down, long now)
    {
        // Wheel and Pause are pulses. Some Print Screen sequences send only a break.
        if (code >= ScrollUp || code == PauseCode || (code == PrintScreenCode && !down && held[code] == 0))
        {
            if (down || code == PrintScreenCode) Pulse(code, now);
            return;
        }
        SetButton(device, code, down, now);
    }

    private void Pulse(int code, long now) => pulseUntil[code] = Math.Max(pulseUntil[code], now + PulseMilliseconds);

    private void RecordRawButton(Device device, int code, bool down, long now)
    {
        (down ? rawDownAt : rawUpAt)[code] = now;
        ApplyButton(device, code, down, now);
        if (hookDevice.Held.Remove(code)) held[code]--;
    }

    internal void Mouse(nint handle, ushort buttons, short wheel, int x, int y, AbsolutePosition? absolute, long now)
    {
        lock (gate)
        {
            var device = GetDevice(handle);
            for (var button = 0; buttons != 0 && button < 5; button++)
            {
                var downFlag = 1 << (button * 2);
                if ((buttons & downFlag) != 0) RecordRawButton(device, MouseStart + button, true, now);
                if ((buttons & (downFlag << 1)) != 0) RecordRawButton(device, MouseStart + button, false, now);
            }
            if ((buttons & 0x0400) != 0 && wheel != 0)
                RecordRawButton(device, wheel > 0 ? ScrollUp : ScrollDown, true, now);

            if (absolute is not null || x != 0 || y != 0) rawMotionAt = now;

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

    internal void ObserveHookButton(int code, bool down, long now)
    {
        lock (gate) hookButtons.Enqueue(new(code, down, now));
    }

    internal void ObserveHookMotion(int x, int y)
    {
        lock (gate)
        {
            if (hookPosition is { } previous)
            {
                hookDeltaX += x - (double)previous.X;
                hookDeltaY += y - (double)previous.Y;
            }
            hookPosition = (x, y);
        }
    }

    private void ApplyUnmatchedHookButtons(long now)
    {
        // Raw Input retains device identity. Give its matching edge time to arrive
        // before using the hook, which has no device handle.
        while (hookButtons.TryPeek(out var button) && button.At <= now - HookMatchMilliseconds)
        {
            hookButtons.Dequeue();
            var code = button.Code;
            var rawEdge = (button.Down ? rawDownAt : rawUpAt)[code];
            var rawOpposite = (button.Down ? rawUpAt : rawDownAt)[code];
            if (rawEdge >= button.At || (rawEdge >= button.At - HookMatchMilliseconds && rawEdge > rawOpposite)) continue;
            if (rawOpposite > button.At)
            {
                if (button.Down) Pulse(code, button.At); // A newer raw release already settled the hold.
                continue;
            }
            if (button.Down && code < ScrollUp && held[code] != 0) continue;
            ApplyButton(hookDevice, code, button.Down, button.At);
            if (!button.Down)
            {
                // A hook-only release can follow a raw press when the foreground changes.
                foreach (var device in devices.Values) SetButton(device, code, false, button.At);
            }
        }
    }

    internal void RemoveDevice(nint handle)
    {
        lock (gate)
        {
            if (devices.Remove(handle, out var device)) ClearHeld(device);
            ClearHeld(hookDevice);
        }
    }

    private void ClearHeld(Device device)
    {
        foreach (var code in device.Held)
            if (--held[code] == 0) pulseUntil[code] = 0;
        device.Held.Clear();
    }

    internal void Clear()
    {
        lock (gate)
        {
            devices.Clear();
            hookDevice.Held.Clear();
            hookButtons.Clear();
            Array.Clear(held);
            Array.Clear(pulseUntil);
            Array.Clear(rawDownAt);
            Array.Clear(rawUpAt);
            hookPosition = null;
            rawMotionAt = long.MinValue;
            deltaX = deltaY = 0;
            hookDeltaX = hookDeltaY = 0;
        }
    }

    internal void WriteFrame(Span<byte> frame, long now, double seconds)
    {
        lock (gate)
        {
            ApplyUnmatchedHookButtons(now);
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
            var useRawMotion = rawMotionAt >= now - HookMatchMilliseconds || deltaX != 0 || deltaY != 0;
            BinaryPrimitives.WriteSingleLittleEndian(frame[67..], (float)((useRawMotion ? deltaX : hookDeltaX) / seconds));
            BinaryPrimitives.WriteSingleLittleEndian(frame[71..], (float)((useRawMotion ? deltaY : hookDeltaY) / seconds));
            deltaX = deltaY = 0;
            hookDeltaX = hookDeltaY = 0;
        }
    }
}
