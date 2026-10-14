namespace VSCDeck.Core;

public sealed record Hotkey(uint Modifiers, uint Key, string Label)
{
    public static Hotkey Parse(string text)
    {
        var parts = text.Trim().Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new FormatException($"Hotkey '{text}' needs a modifier and a key.");
        uint modifiers = 0;
        foreach (var part in parts[..^1])
            modifiers |= part.ToUpperInvariant() switch
            {
                "ALT" => 1u, "CTRL" or "CONTROL" => 2u, "SHIFT" => 4u, "WIN" or "WINDOWS" => 8u,
                _ => throw new FormatException($"Unknown modifier '{part}'.")
            };
        var last = parts[^1].ToUpperInvariant();
        uint key;
        if (last.Length == 1 && (last[0] is >= '0' and <= '9' or >= 'A' and <= 'Z'))
            key = last[0];
        else if (last.StartsWith('F') && int.TryParse(last[1..], out var function) && function is >= 1 and <= 24)
            key = (uint)(0x70 + function - 1);
        else throw new FormatException($"Unsupported hotkey key '{last}'. Use a digit, letter, or F1-F24.");
        if (key == 0x7B) throw new FormatException("F12 is reserved by Windows for debugging.");
        return new(modifiers, key, text.Trim());
    }

    public static List<Hotkey> ParseList(string text)
    {
        var hotkeys = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Parse).ToList();
        if (hotkeys.Count == 0) throw new FormatException("Enter at least one hotkey.");
        if (hotkeys.Select(h => (h.Modifiers, h.Key)).Distinct().Count() != hotkeys.Count)
            throw new FormatException("Each position must have a different hotkey.");
        return hotkeys;
    }
}
