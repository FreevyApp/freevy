using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Freevy.Core;
using Freevy.Core.Macros;
using static Freevy.App.Native;

namespace Freevy.App;

/// <summary>Replaces typed text in the focused application with injected input.</summary>
internal sealed class OutputSender(int clipboardThreshold, LineBreakMode lineBreaks)
{
    /// <summary>
    /// Deletes the abbreviation, then runs a macro plan: text, key presses (%(Key Shift+Enter)…),
    /// waits, launches, then retypes the delimiter and finally moves the cursor back for %(cursor).
    /// </summary>
    public async Task RunAsync(string deleted, OutputPlan plan, string suffix, LineBreakMode? snippetLineBreaks = null)
    {
        Replace(deleted, "");
        foreach (var step in plan.Steps)
        {
            switch (step)
            {
                case TypeText text:
                    Replace("", text.Text, "", snippetLineBreaks);
                    break;
                case PressKeys keys:
                    SendKeys(keys);
                    break;
                case Wait wait:
                    await Task.Delay(wait.Duration);
                    break;
                case LaunchTarget launch:
                    Process.Start(new ProcessStartInfo(launch.Target, launch.Arguments) { UseShellExecute = true });
                    break;
            }
        }

        Replace("", "", suffix);
        var back = plan.CursorBack > 0 ? plan.CursorBack + new StringInfo(suffix).LengthInTextElements : 0;
        if (back > 0)
        {
            var inputs = new List<INPUT>();
            for (var i = 0; i < back; i++)
            {
                AddKey(inputs, 0x25); // Left
            }

            Send(inputs);
        }
    }

    private static void SendKeys(PressKeys keys)
    {
        var codes = keys.Keys.Select(KeyNames.VirtualKey).ToList();
        var inputs = new List<INPUT>();
        switch (keys.Action)
        {
            case KeyAction.Down:
                codes.ForEach(vk => inputs.Add(Key(vk, 0)));
                break;
            case KeyAction.Up:
                codes.ForEach(vk => inputs.Add(Key(vk, KEYEVENTF_KEYUP)));
                break;
            default:
                // A chord: press in order, release in reverse, like a person would.
                codes.ForEach(vk => inputs.Add(Key(vk, 0)));
                for (var i = codes.Count - 1; i >= 0; i--)
                {
                    inputs.Add(Key(codes[i], KEYEVENTF_KEYUP));
                }

                break;
        }

        Send(inputs);
    }

    /// <summary>
    /// Deletes <paramref name="deleted"/>, inserts <paramref name="text"/> (typed, or pasted when it is
    /// long or has line breaks in paste mode), then types <paramref name="suffix"/>, the delimiter
    /// the user pressed, so an Enter delimiter still acts as Enter. <paramref name="snippetLineBreaks"/>
    /// is the snippet's own choice and wins over the setting.
    /// </summary>
    public void Replace(string deleted, string text, string suffix = "", LineBreakMode? snippetLineBreaks = null)
    {
        var mode = snippetLineBreaks ?? lineBreaks;
        var inputs = new List<INPUT>();
        // One backspace per user-perceived character, so emoji and combined characters delete cleanly.
        var backspaces = new StringInfo(deleted).LengthInTextElements;
        for (var i = 0; i < backspaces; i++)
        {
            AddKey(inputs, VK_BACK);
        }

        if (text.Length > clipboardThreshold || (mode == LineBreakMode.Paste && text.Contains('\n')))
        {
            Send(inputs);
            Paste(text);
        }
        else
        {
            AddText(inputs, text, shiftEnter: mode == LineBreakMode.ShiftEnter);
        }

        AddText(inputs, suffix, shiftEnter: false);
        Send(inputs);
    }

    private static void AddText(List<INPUT> inputs, string text, bool shiftEnter)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '\n' when shiftEnter:
                    inputs.Add(Key(VK_SHIFT, 0));
                    AddKey(inputs, VK_RETURN);
                    inputs.Add(Key(VK_SHIFT, KEYEVENTF_KEYUP));
                    break;
                case '\n':
                    AddKey(inputs, VK_RETURN);
                    break;
                case '\r':
                    // Windows line endings: the \n that follows is the line break.
                    break;
                case '\t':
                    AddKey(inputs, VK_TAB);
                    break;
                default:
                    AddUnicode(inputs, c);
                    break;
            }
        }
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
        new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags, dwExtraInfo = OwnInputMarker } } };

    private static INPUT Unicode(char c, uint flags) =>
        new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = flags, dwExtraInfo = OwnInputMarker } } };

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
