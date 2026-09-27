namespace Vanta.Core;

/// <summary>"Ctrl+Shift+Numpad+" &lt;-&gt; (modifiers, virtual-key) for RegisterHotKey. Same grammar as the schema/UI.</summary>
public static class HotkeyParser
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Numpad+"] = 0x6B, ["Numpad-"] = 0x6D, ["Numpad*"] = 0x6A, ["Numpad/"] = 0x6F, ["Numpad."] = 0x6E,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27, ["Space"] = 0x20, ["Tab"] = 0x09, ["Pause"] = 0x13,
    };

    public static bool TryParse(string? combo, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        var s = combo.Trim();
        // split on '+' but keep a trailing "Numpad+" key intact
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '+' && i > start && i != s.Length - 1) { parts.Add(s[start..i]); start = i + 1; }
        parts.Add(s[start..]);
        for (int i = 0; i < parts.Count - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                default: return false;
            }
        }
        var key = parts[^1].Trim();
        if (Named.TryGetValue(key, out vk)) return true;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) { vk = char.ToUpperInvariant(key[0]); return true; }
        if (key.Length >= 2 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24) { vk = (uint)(0x70 + f - 1); return true; }
        if (key.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) && key.Length == 7 && char.IsAsciiDigit(key[6])) { vk = (uint)(0x60 + key[6] - '0'); return true; }
        return false;
    }
}
