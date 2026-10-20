using System.Globalization;
using System.Runtime.InteropServices;
using static Freevy.App.Native;

namespace Freevy.App;

/// <summary>Replaces typed text in the focused application with injected input.</summary>
internal sealed class OutputSender(int clipboardThreshold)
{
    public void Replace(string deleted, string text)
    {
        var inputs = new List<INPUT>();
        // One backspace per user-perceived character, so emoji and combined characters delete cleanly.
        var backspaces = new StringInfo(deleted).LengthInTextElements;
        for (var i = 0; i < backspaces; i++)
        {
            AddKey(inputs, VK_BACK);
        }

        if (text.Length > clipboardThreshold)
        {
            Send(inputs);
            Paste(text);
            return;
        }

        foreach (var c in text)
        {
            switch (c)
            {
                case '\n':
                    AddKey(inputs, VK_RETURN);
                    break;
                case '\t':
                    AddKey(inputs, VK_TAB);
                    break;
                default:
                    AddUnicode(inputs, c);
                    break;
            }
        }

        Send(inputs);
    }

    private static void Paste(string text)
    {
        var previous = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        Clipboard.SetText(text);

        var inputs = new List<INPUT>();
        inputs.Add(Key(VK_CONTROL, 0));
        AddKey(inputs, VK_V);
        inputs.Add(Key(VK_CONTROL, KEYEVENTF_KEYUP));
        Send(inputs);

        // Give the target app time to read the clipboard before restoring it.
        Thread.Sleep(150);
        if (previous is null)
        {
            Clipboard.Clear();
        }
        else
        {
            Clipboard.SetText(previous);
        }
    }

    private static void AddKey(List<INPUT> inputs, int vk)
    {
        inputs.Add(Key(vk, 0));
        inputs.Add(Key(vk, KEYEVENTF_KEYUP));
    }

    private static void AddUnicode(List<INPUT> inputs, char c)
    {
        inputs.Add(Unicode(c, KEYEVENTF_UNICODE));
        inputs.Add(Unicode(c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
    }

    private static INPUT Key(int vk, uint flags) =>
        new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags } } };

    private static INPUT Unicode(char c, uint flags) =>
        new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = flags } } };

    private static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        var array = inputs.ToArray();
        SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
        inputs.Clear();
    }
}
