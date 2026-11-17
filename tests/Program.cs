using System.Buffers.Binary;
using StreamUI.DesktopInput;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var input = new InputState();
var frame = new byte[InputState.FrameSize];
bool HasKey(int offset, int code) => (frame[offset + code / 8] & (1 << (code % 8))) != 0;
bool Held(int code) => HasKey(3, code);
bool Lit(int code) => HasKey(35, code);
float X() => BinaryPrimitives.ReadSingleLittleEndian(frame.AsSpan(67));
float Y() => BinaryPrimitives.ReadSingleLittleEndian(frame.AsSpan(71));
const int w = 0x11;

input.Key(1, w, true, 100);
input.Key(1, w, true, 700); // Windows typematic repeat.
input.WriteFrame(frame, 900, .02);
Check(frame[0] == 1 && Held(w) && Lit(w), "A held key remains active after the pulse expires.");
input.Key(1, w, false, 901);
input.WriteFrame(frame, 920, .02);
Check(!Held(w) && !Lit(w), "One release clears a long press despite auto-repeat.");

input.Key(1, w, true, 1000);
input.Key(1, w, false, 1001);
input.WriteFrame(frame, 1020, .02);
Check(!Held(w) && Lit(w), "A short press survives until the next render frame.");
input.Key(1, w, true, 1100);
input.Key(2, w, true, 1101);
input.RemoveDevice(1);
input.WriteFrame(frame, 1200, .02);
Check(Held(w), "Removing one keyboard preserves a key held by another.");
input.RemoveDevice(2);
input.WriteFrame(frame, 1220, .02);
Check(!Held(w) && !Lit(w), "Unplugging the last device clears its keys.");

input.Key(1, InputState.PauseCode, true, 1300);
input.Key(1, InputState.PrintScreenCode, false, 1300);
input.WriteFrame(frame, 1320, .02);
Check(Lit(InputState.PauseCode) && !Held(InputState.PauseCode), "Pause pulses without a stuck hold.");
Check(Lit(InputState.PrintScreenCode) && !Held(InputState.PrintScreenCode), "Break-only Print Screen pulses.");
Check(RawInputCapture.ScanCode(new() { MakeCode = 0x48, Flags = 2, VirtualKey = 0x26 }) == 0xc8, "Arrow Up differs from numpad 8.");
Check(RawInputCapture.ScanCode(new() { MakeCode = 0x48, VirtualKey = 0x68 }) == 0x48, "Numpad 8 uses its physical position.");
Check(RawInputCapture.ScanCode(new() { MakeCode = 0x1d, Flags = 2, VirtualKey = 0x11 }) == 0x9d, "Right Ctrl is distinct from left Ctrl.");
Check(RawInputCapture.ScanCode(new() { MakeCode = 0x45, Flags = 2, VirtualKey = 0x90 }) == 0x45, "Extended Num Lock is distinct from Pause.");
Check(RawInputCapture.ScanCode(new() { MakeCode = 0x2a, Flags = 2, VirtualKey = 0x10 }) == 0, "Fake extended Shift is ignored.");

input.Mouse(3, 0x0155, 0, 30, -40, null, 1400); // All five buttons down.
input.Mouse(3, 0x0400, 120, -10, 4, null, 1401);
input.Mouse(3, 0x0400, -120, 0, 0, null, 1402);
input.WriteFrame(frame, 1420, .02);
Check(frame[1] == 0x1f && frame[2] == 0x7f, "Five mouse buttons and both scroll directions have independent bits.");
Check(X() == 1000 && Y() == -1800, "Relative motion accumulates and keeps its direction.");
input.Mouse(3, 0x02aa, 0, 0, 0, null, 1500);
input.WriteFrame(frame, 1520, .02);
Check(frame[1] == 0 && frame[2] == 0 && X() == 0 && Y() == 0, "Button releases and idle motion clear the frame.");

input.Mouse(4, 0, 0, 0, 0, new(20000, 40000, 2000, 1000, true), 1600);
input.WriteFrame(frame, 1620, .02);
Check(X() == 0 && Y() == 0, "An absolute device starts without a phantom movement.");
input.Mouse(4, 0, 0, 0, 0, new(21000, 38000, 2000, 1000, true), 1621);
input.WriteFrame(frame, 1640, .02);
Check(X() > 0 && X() == -Y(), "Absolute deltas account for desktop dimensions.");
input.Clear();
input.WriteFrame(frame, 1700, .02);
Check(frame.AsSpan(1).IndexOfAnyExcept((byte)0) < 0, "Stopping clears the complete input snapshot.");

input.ObserveHookButton(w, true, 1800);
input.ObserveHookButton(w, false, 1802);
input.ObserveHookButton(InputState.MouseStart + 4, true, 1803);
input.ObserveHookButton(InputState.ScrollDown, true, 1804);
input.ObserveHookMotion(300, 200);
input.ObserveHookMotion(310, 196);
input.WriteFrame(frame, 1830, .02);
Check(!Held(w) && Lit(w), "A hook-only short key press keeps its display pulse.");
Check(frame[1] == 0x10 && frame[2] == 0x50 && X() == 500 && Y() == -200,
    "Hook-only side buttons, wheel and cursor movement reach the same binary protocol.");
input.Mouse(3, 0x0200, 0, 6, 3, null, 1840);
input.ObserveHookMotion(340, 210);
input.WriteFrame(frame, 1850, .02);
Check(frame[1] == 0 && X() == 300 && Y() == 150, "Raw release clears a hook hold, and raw motion takes precedence over cursor motion.");

input.Key(1, w, true, 1900);
input.ObserveHookButton(w, true, 1901); // Keyboard hook arrives after Raw Input.
input.Key(2, w, true, 2000);
input.ObserveHookButton(w, false, 2010);
input.Key(1, w, false, 2011); // Raw Input arrives after the release hook.
input.WriteFrame(frame, 2040, .02);
Check(Held(w), "A matched hook release preserves another physical keyboard's hold.");
input.ObserveHookButton(w, false, 2050);
input.WriteFrame(frame, 2080, .02);
Check(!Held(w) && !Lit(w), "An unmatched hook release clears a press started by Raw Input.");

input.ObserveHookButton(w, true, 2100);
input.Key(1, w, true, 2101);
input.RemoveDevice(1);
input.WriteFrame(frame, 2130, .02);
Check(!Held(w) && !Lit(w), "A queued duplicate hook press cannot restore an unplugged keyboard's hold.");

input.ObserveHookButton(w, true, 2200);
input.WriteFrame(frame, 2300, .02);
Check(Held(w) && Lit(w), "A hook-only hold stays visible after the initial pulse expires.");
input.ObserveHookButton(w, false, 2301);
input.WriteFrame(frame, 2330, .02);
Check(!Held(w) && !Lit(w), "A hook-only release clears the hold.");
Console.WriteLine("Input state and binary frame checks passed.");
