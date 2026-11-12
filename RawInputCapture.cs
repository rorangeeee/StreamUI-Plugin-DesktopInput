using System.ComponentModel;
using System.Runtime.InteropServices;
using static StreamUI.DesktopInput.Win32;

namespace StreamUI.DesktopInput;

internal sealed class RawInputCapture : IAsyncDisposable
{
    private const uint CloseMessage = 0x0010;
    private const uint DeviceChangeMessage = 0x00fe;
    private const uint InputMessage = 0x00ff;
    private const uint ReadInput = 0x10000003;
    private const uint InputSink = 0x00000100;
    private const uint DeviceNotify = 0x00002000;
    private const uint Remove = 0x00000001;
    private const int MessageOnlyWindow = -3;

    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<RawHeader>();
    private static readonly uint InputSize = (uint)Marshal.SizeOf<RawInput>();
    private readonly InputState input;
    private readonly TaskCompletionSource<nint> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Started => started.Task;

    internal RawInputCapture(InputState input)
    {
        this.input = input;
        new Thread(Run) { IsBackground = true, Name = "StreamUI Raw Input" }.Start();
    }

    private void Run()
    {
        var instance = GetModuleHandleW(null);
        var windowClass = new WindowClass { Instance = instance, Name = "StreamUI.RawInput." + Guid.NewGuid(), Procedure = HandleMessage };
        ushort atom = 0;
        nint window = 0;
        var registered = false;
        Exception? failure = null;
        try
        {
            atom = RegisterClassW(in windowClass);
            if (atom == 0) throw LastError(nameof(RegisterClassW));
            window = CreateWindowExW(0, windowClass.Name, null, 0, 0, 0, 0, 0, MessageOnlyWindow, 0, instance, 0);
            if (window == 0) throw LastError(nameof(CreateWindowExW));
            registered = RegisterDevices(window, InputSink | DeviceNotify);
            if (!registered) throw LastError(nameof(RegisterRawInputDevices));
            started.TrySetResult(window);

            int result;
            while ((result = GetMessageW(out var message, 0, 0, 0)) > 0) DispatchMessageW(in message);
            if (result < 0) throw LastError(nameof(GetMessageW));
        }
        catch (Exception error)
        {
            if (!started.TrySetException(error)) failure = error;
        }
        finally
        {
            if (registered) RegisterDevices(0, Remove);
            if (window != 0) DestroyWindow(window);
            if (atom != 0) UnregisterClassW(windowClass.Name, instance);
            GC.KeepAlive(windowClass.Procedure);
            input.Clear();
            if (failure is null) finished.TrySetResult();
            else finished.TrySetException(failure);
        }
    }

    private static bool RegisterDevices(nint window, uint flags) => RegisterRawInputDevices(
        [new RawDevice(2, flags, window), new RawDevice(6, flags, window)], 2, (uint)Marshal.SizeOf<RawDevice>());

    private static Win32Exception LastError(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, operation + ": " + new Win32Exception(code).Message);
    }

    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == CloseMessage)
        {
            PostQuitMessage(0);
            return 0;
        }
        if (message == InputMessage) ReadPacket(lParam);
        if (message == DeviceChangeMessage && wParam == 2) input.RemoveDevice(lParam);
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private void ReadPacket(nint handle)
    {
        var size = InputSize;
        if (GetRawInputData(handle, ReadInput, out var packet, ref size, HeaderSize) == uint.MaxValue) return;
        var now = Environment.TickCount64;
        var device = packet.Header.Device;
        if (packet.Header.Type == 1)
        {
            var key = packet.Payload.Keyboard;
            var code = ScanCode(key);
            if (code != 0) input.Key(device, code, (key.Flags & 1) == 0, now);
        }
        else if (packet.Header.Type == 0)
        {
            var mouse = packet.Payload.Mouse;
            if ((mouse.Flags & 4) != 0) return; // Attributes changed, no motion sample.
            InputState.AbsolutePosition? absolute = null;
            if ((mouse.Flags & 1) != 0)
            {
                var virtualDesktop = (mouse.Flags & 2) != 0;
                var width = GetSystemMetrics(virtualDesktop ? 78 : 0);
                var height = GetSystemMetrics(virtualDesktop ? 79 : 1);
                absolute = new(mouse.X, mouse.Y, width, height, virtualDesktop);
            }
            input.Mouse(device, mouse.Buttons, mouse.Wheel, mouse.X, mouse.Y, absolute, now);
        }
    }

    internal static int ScanCode(RawKeyboard key)
    {
        if (key.VirtualKey >= 0xff || key.MakeCode == 0xff) return 0;
        if (key.VirtualKey is 0x13 or 0x03) return InputState.PauseCode;
        if (key.VirtualKey == 0x2c) return InputState.PrintScreenCode;
        if (key.VirtualKey == 0x90) return 0x45; // Num Lock can arrive with E0 on some devices.
        if ((key.Flags & 4) != 0) return 0;
        var scan = key.MakeCode & 0x7f;
        var extended = (key.Flags & 2) != 0;
        if (scan == 0)
        {
            // Some consumer/media keys report only a virtual key.
            var mapped = MapVirtualKeyW(key.VirtualKey, 4);
            scan = (int)mapped & 0x7f;
            extended = (mapped & 0xff00) == 0xe000;
        }
        if (scan == 0 || (extended && scan is 0x2a or 0x36)) return 0;
        return scan | (extended ? 0x80 : 0);
    }

    public async ValueTask DisposeAsync()
    {
        await ((Task)started.Task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (started.Task.IsCompletedSuccessfully && !finished.Task.IsCompleted)
            PostMessageW(started.Task.Result, CloseMessage, 0, 0);
        await finished.Task;
    }
}
