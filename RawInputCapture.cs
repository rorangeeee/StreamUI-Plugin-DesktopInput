using System.ComponentModel;
using System.Diagnostics;
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
    private const int LowLevelKeyboard = 13;
    private const int LowLevelMouse = 14;

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
        nint keyboardHook = 0, mouseHook = 0;
        HookProcedure keyboardProcedure = HandleKeyboardHook, mouseProcedure = HandleMouseHook;
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
            keyboardHook = InstallHook(LowLevelKeyboard, keyboardProcedure, instance);
            mouseHook = InstallHook(LowLevelMouse, mouseProcedure, instance);
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
            if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != 0) UnhookWindowsHookEx(keyboardHook);
            if (registered) RegisterDevices(0, Remove);
            if (window != 0) DestroyWindow(window);
            if (atom != 0) UnregisterClassW(windowClass.Name, instance);
            GC.KeepAlive(windowClass.Procedure);
            GC.KeepAlive(keyboardProcedure);
            GC.KeepAlive(mouseProcedure);
            input.Clear();
            if (failure is null) finished.TrySetResult();
            else finished.TrySetException(failure);
        }
    }

    private static nint InstallHook(int type, HookProcedure procedure, nint instance)
    {
        var hook = SetWindowsHookExW(type, procedure, instance, 0);
        if (hook == 0) Trace.TraceWarning(LastError($"SetWindowsHookExW({type})").Message);
        return hook;
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

    private nint HandleKeyboardHook(int code, nuint message, nint data)
    {
        if (code == 0)
        {
            var key = Marshal.PtrToStructure<KeyboardHookData>(data);
            // VK_PACKET carries Unicode text, not a physical key position.
            if (key.VirtualKey != 0xe7)
            {
                var scan = ScanCode(new()
                {
                    MakeCode = (ushort)key.ScanCode,
                    VirtualKey = (ushort)key.VirtualKey,
                    Flags = (ushort)((key.Flags & 1) << 1), // LLKHF_EXTENDED to RI_KEY_E0.
                });
                if (scan != 0) input.ObserveHookButton(scan, (key.Flags & 0x80) == 0, Environment.TickCount64);
            }
        }
        return CallNextHookEx(0, code, message, data);
    }

    private nint HandleMouseHook(int code, nuint message, nint data)
    {
        if (code == 0)
        {
            var mouse = Marshal.PtrToStructure<MouseHookData>(data);
            var now = Environment.TickCount64;
            var detail = (short)(mouse.MouseData >> 16);
            switch (message)
            {
                case 0x0200: input.ObserveHookMotion(mouse.X, mouse.Y); break;
                case 0x0201: case 0x0202: input.ObserveHookButton(InputState.MouseStart, message == 0x0201, now); break;
                case 0x0204: case 0x0205: input.ObserveHookButton(InputState.MouseStart + 1, message == 0x0204, now); break;
                case 0x0207: case 0x0208: input.ObserveHookButton(InputState.MouseStart + 2, message == 0x0207, now); break;
                case 0x020b: case 0x020c:
                    if (detail is 1 or 2) input.ObserveHookButton(InputState.MouseStart + 2 + detail, message == 0x020b, now);
                    break;
                case 0x020a:
                    if (detail != 0) input.ObserveHookButton(detail > 0 ? InputState.ScrollUp : InputState.ScrollDown, true, now);
                    break;
            }
        }
        return CallNextHookEx(0, code, message, data);
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
