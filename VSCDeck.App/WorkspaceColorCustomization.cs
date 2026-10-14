using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace VSCDeck.App;

internal static class WorkspaceColorCustomization
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static string? ReadLocal(string workspace)
    {
        var path = SettingsPath(workspace);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
    }

    internal static string SettingsPath(string workspace) =>
        workspace.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase)
            ? workspace
            : Path.Combine(workspace, ".vscode", "settings.json");

    internal static string? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var document = JsonDocument.Parse(text, JsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Workspace settings must be a JSON object.");
        var settings = document.RootElement;
        if (settings.TryGetProperty("settings", out var workspaceSettings) &&
            workspaceSettings.ValueKind == JsonValueKind.Object)
            settings = workspaceSettings;
        if (!settings.TryGetProperty("workbench.colorCustomizations", out var customizations))
            return null;
        if (customizations.ValueKind != JsonValueKind.Object)
            throw new JsonException("workbench.colorCustomizations must be a JSON object.");
        if (!customizations.TryGetProperty("statusBar.background", out var color) ||
            color.ValueKind == JsonValueKind.Null)
            return null;
        if (color.ValueKind != JsonValueKind.String || color.GetString() is not { } value)
            throw new JsonException("statusBar.background must be a color string.");
        return Normalize(value);
    }

    internal static Color ToColor(string normalized) => Color.FromArgb(
        byte.Parse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(normalized.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static string Normalize(string value)
    {
        if (!value.StartsWith('#'))
            throw new FormatException("statusBar.background must use a hexadecimal VS Code color.");
        var hex = value[1..];
        if (hex.Any(character => !Uri.IsHexDigit(character)))
            throw new FormatException("statusBar.background contains a non-hexadecimal character.");
        string red;
        string green;
        string blue;
        string alpha;
        switch (hex.Length)
        {
            case 3:
                red = new(hex[0], 2);
                green = new(hex[1], 2);
                blue = new(hex[2], 2);
                alpha = "FF";
                break;
            case 4:
                red = new(hex[0], 2);
                green = new(hex[1], 2);
                blue = new(hex[2], 2);
                alpha = new(hex[3], 2);
                break;
            case 6:
                red = hex[..2];
                green = hex[2..4];
                blue = hex[4..6];
                alpha = "FF";
                break;
            case 8:
                red = hex[..2];
                green = hex[2..4];
                blue = hex[4..6];
                alpha = hex[6..8];
                break;
            default:
                throw new FormatException(
                    "statusBar.background must use #RGB, #RGBA, #RRGGBB, or #RRGGBBAA.");
        }
        return ("#" + alpha + red + green + blue).ToUpperInvariant();
    }
}
