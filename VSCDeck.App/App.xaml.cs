using System.IO;
using System.Threading;
using System.Windows;

namespace VSCDeck.App;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\VSCDeck-" + Environment.UserName, out var created);
        if (!created)
        {
            MessageBox.Show("VSC Deck is already running.", "VSC Deck");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            AppStorage.Log(args.Exception.ToString());
            MessageBox.Show("VSC Deck encountered an error:\n" + args.Exception.Message +
                "\n\n" + (AppStorage.LoggingWarning ?? "See " + AppStorage.LogPath),
                "VSC Deck error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            args.Handled = true;
        };
        try
        {
            var fresh = !File.Exists(AppStorage.SettingsPath);
            var settings = AppStorage.Load();
            if (fresh)
            {
                var discovery = Task.Run(RemoteSourceDiscovery.Discover).GetAwaiter().GetResult();
                settings.RemoteSources = discovery.Sources.ToList();
                foreach (var warning in discovery.Warnings) AppStorage.Log(warning);
                AppStorage.Save(settings);
            }
            MainWindow = new DeckWindow(settings);
            MainWindow.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or
            FormatException or System.ComponentModel.Win32Exception)
        {
            AppStorage.Log(ex.ToString());
            MessageBox.Show("Unable to load VSC Deck settings:\n" + ex.Message +
                (AppStorage.LoggingWarning is { } warning ? "\n\n" + warning : ""), "VSC Deck error");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
