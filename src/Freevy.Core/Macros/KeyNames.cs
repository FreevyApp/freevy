namespace Freevy.Core.Macros;

/// <summary>Key names the %(Key) macros accept, with their Windows virtual-key codes (Breevy's names plus a few synonyms).</summary>
public static class KeyNames
{
    private static readonly Dictionary<string, int> Codes = Build();

    public static bool IsKnown(string name) => Codes.ContainsKey(name.ToLowerInvariant());

    public static int VirtualKey(string name) => Codes[name.ToLowerInvariant()];

    public static bool IsModifier(string name) => name.ToLowerInvariant() is "shift" or "ctrl" or "control" or "alt" or "win";

    private static Dictionary<string, int> Build()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["shift"] = 0x10, ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["win"] = 0x5B,
            ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
            ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
            ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27, ["space"] = 0x20,
            ["menu"] = 0x5D, ["apps"] = 0x5D, ["capslock"] = 0x14, ["numlock"] = 0x90, ["printscreen"] = 0x2C,
        };
        for (var f = 1; f <= 24; f++) map[$"f{f}"] = 0x70 + f - 1;
        for (var c = 'a'; c <= 'z'; c++) map[c.ToString()] = c - 'a' + 0x41;
        for (var d = '0'; d <= '9'; d++) map[d.ToString()] = d - '0' + 0x30;
        return map;
    }
}
