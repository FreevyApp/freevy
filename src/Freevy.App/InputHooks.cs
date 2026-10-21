using System.Runtime.InteropServices;
using static Freevy.App.Native;

namespace Freevy.App;

/// <summary>
/// Global low-level keyboard and mouse hooks. Translates key presses into the events the
/// engine understands. Callbacks run on the thread that installed the hooks (the UI thread),
/// so they must stay fast and never inject input themselves.
/// </summary>
internal sealed class InputHooks : IDisposable
{
    // Keys that move the caret or abandon the current word.
    private static readonly HashSet<int> ResetKeys =
    [
        0x1B, // Escape
        0x21, 0x22, 0x23, 0x24, // PageUp, PageDown, End, Home
        0x25, 0x26, 0x27, 0x28, // arrows
        0x2E, // Delete
    ];

    // Held by themselves these produce no character and must not disturb the buffer.
    private static readonly HashSet<int> ModifierKeys =
    [
        VK_SHIFT, VK_CONTROL, VK_MENU, VK_CAPITAL,
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, // left/right Shift, Ctrl, Alt
    ];

    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private readonly IntPtr _keyboardHook;
    private readonly IntPtr _mouseHook;
    private readonly byte[] _keyState = new byte[256];
    private readonly char[] _chars = new char[8];
    private IntPtr _lastWindow;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    public event Action<char>? CharTyped;
    public event Action? Backspace;
    public event Action? ContextLost;

    public InputHooks()
    {
        // Keep delegates in fields so the GC cannot collect them while Windows still calls them.
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;
        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Installing input hooks failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // Freevy's own output must never trigger it again; keys injected by anything else count as typing.
            if ((data.flags & LLKHF_INJECTED) == 0 || data.dwExtraInfo != OwnInputMarker)
            {
                HandleKey(data);
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN || wParam == WM_MBUTTONDOWN))
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLMHF_INJECTED) == 0)
            {
                ContextLost?.Invoke();
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void HandleKey(KBDLLHOOKSTRUCT data)
    {
        var vk = (int)data.vkCode;
        var window = GetForegroundWindow();
        if (window != _lastWindow)
        {
            _lastWindow = window;
            ContextLost?.Invoke();
        }

        if (ModifierKeys.Contains(vk))
        {
            return;
        }

        // Never expand inside Freevy's own windows, above all the master password prompt.
        GetWindowThreadProcessId(window, out var processId);
        if (processId == _ownProcessId)
        {
            ContextLost?.Invoke();
            return;
        }

        if (vk == VK_BACK)
        {
            Backspace?.Invoke();
            return;
        }

        var ctrl = IsDown(VK_CONTROL);
        var alt = IsDown(VK_MENU);
        var win = IsDown(VK_LWIN) || IsDown(VK_RWIN);
        // Ctrl+Alt together is AltGr on many layouts (Turkish, German) and still types characters.
        var shortcut = win || (ctrl ^ alt);
        if (ResetKeys.Contains(vk) || shortcut)
        {
            ContextLost?.Invoke();
            return;
        }

        var count = Translate(data, window);
        if (count <= 0)
        {
            // Dead keys (-1) and non-character keys (0) produce nothing to match yet.
            return;
        }

        for (var i = 0; i < count; i++)
        {
            CharTyped?.Invoke(_chars[i]);
        }
    }

    private int Translate(KBDLLHOOKSTRUCT data, IntPtr window)
    {
        Array.Clear(_keyState);
        if (IsDown(VK_SHIFT)) _keyState[VK_SHIFT] = 0x80;
        if (IsDown(VK_CONTROL)) _keyState[VK_CONTROL] = 0x80;
        if (IsDown(VK_MENU)) _keyState[VK_MENU] = 0x80;
        if ((GetKeyState(VK_CAPITAL) & 1) != 0) _keyState[VK_CAPITAL] = 0x01;

        var layout = GetKeyboardLayout(GetWindowThreadProcessId(window, out _));
        // Flag 4: do not change keyboard state, so dead keys keep working in the target app.
        return ToUnicodeEx(data.vkCode, data.scanCode, _keyState, _chars, _chars.Length, 4, layout);
    }

    public void Dispose()
    {
        UnhookWindowsHookEx(_keyboardHook);
        UnhookWindowsHookEx(_mouseHook);
    }
}
