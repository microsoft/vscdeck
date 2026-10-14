using System.IO;
using System.Text.Json;

namespace VSDeck.App;

public sealed class Settings
{
    public int ButtonSize { get; set; } = 64;
    public string Hotkeys { get; set; } =
        "Ctrl+Shift+1, Ctrl+Shift+2, Ctrl+Shift+3, Ctrl+Shift+4, Ctrl+Shift+5, Ctrl+Shift+6, Ctrl+Shift+7, Ctrl+Shift+8, Ctrl+Shift+9, Ctrl+Shift+0";
    public List<string> Order { get; set; } = [];
    public List<RemoteSource> RemoteSources { get; set; } = [];
    public bool SnapToScreenEdges { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
}

public static class AppStorage
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSDeck");
    public static string LogPath => Path.Combine(DirectoryPath, "vsdeck.log");
    public static string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly LogWriter Logger = new(LogPath);
    public static string? LoggingWarning => Logger.Warning;

    public static Settings Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(SettingsPath)) return new();
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))
            ?? throw new JsonException("Settings must be a JSON object.");
        var migrateSize = settings.ButtonSize == 30;
        if (migrateSize) settings.ButtonSize = 32;
        if (settings.ButtonSize is not (32 or 64))
            throw new JsonException("ButtonSize must be 32 or 64.");
        if (settings.Order is null || settings.Hotkeys is null || settings.RemoteSources is null)
            throw new JsonException("Order, Hotkeys, and RemoteSources cannot be null.");
        foreach (var remote in settings.RemoteSources) remote.Validate();
        if (settings.RemoteSources.Select(s => s.Identity).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.RemoteSources.Count)
            throw new FormatException("Each remote workspace source must be unique.");
        _ = Core.Hotkey.ParseList(settings.Hotkeys);
        if (migrateSize) Save(settings);
        return settings;
    }

    public static void Save(Settings settings)
    {
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, SettingsPath, true);
    }

    public static void WriteDiagnostic(object value) =>
        File.WriteAllText(Path.Combine(DirectoryPath, "status.json"), JsonSerializer.Serialize(value, JsonOptions));

    public static void Log(string message) => Logger.Write(message);
}
