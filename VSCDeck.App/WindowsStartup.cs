using System.IO;
using Microsoft.Win32;

namespace VSCDeck.App;

internal static class WindowsStartup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VSCDeck";

    public static void SetEnabled(bool enabled)
    {
        var executablePath = Environment.ProcessPath ??
            throw new InvalidOperationException("The VSC Deck executable path is unavailable.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true) ??
            throw new IOException("The current-user Windows startup registry key could not be opened.");
        SetEnabled(key, ValueName, enabled, executablePath);
    }

    internal static void SetEnabled(RegistryKey key, string valueName, bool enabled,
        string executablePath)
    {
        if (enabled)
            key.SetValue(valueName, $"\"{executablePath}\"", RegistryValueKind.String);
        else
            key.DeleteValue(valueName, false);
    }
}
