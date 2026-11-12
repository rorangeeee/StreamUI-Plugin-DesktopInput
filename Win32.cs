using System.Runtime.InteropServices;

namespace StreamUI.DesktopInput;

internal static class Win32
{
    internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Style;
        internal WindowProcedure Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background, MenuName;
        internal string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawDevice(ushort usage, uint flags, nint target)
    {
        internal ushort UsagePage = 1;
        internal ushort Usage = usage;
        internal uint Flags = flags;
        internal nint Target = target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawHeader
    {
        internal uint Type, Size;
        internal nint Device;
        internal nuint WParam;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct RawMouse
    {
        [FieldOffset(0)] internal ushort Flags;
        [FieldOffset(4)] internal ushort Buttons;
        [FieldOffset(6)] internal short Wheel;
        [FieldOffset(12)] internal int X;
        [FieldOffset(16)] internal int Y;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct RawKeyboard
    {
        [FieldOffset(0)] internal ushort MakeCode;
        [FieldOffset(2)] internal ushort Flags;
        [FieldOffset(6)] internal ushort VirtualKey;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct RawPayload
    {
        [FieldOffset(0)] internal RawMouse Mouse;
        [FieldOffset(0)] internal RawKeyboard Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInput
    {
        internal RawHeader Header;
        internal RawPayload Payload;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern nint GetModuleHandleW(string? module);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern ushort RegisterClassW(in WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterClassW(string name, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern nint CreateWindowExW(uint exStyle, string className, string? title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern int GetMessageW(out Message message, nint window, uint first, uint last);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint DispatchMessageW(in Message message);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern uint GetRawInputData(nint input, uint command, out RawInput data, ref uint size, uint headerSize);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetSystemMetrics(int index);
}
