using System.IO;
using System.Threading;
using System.Windows;

namespace VSDeck.App;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\VSDeck-" + Environment.UserName, out var created);
        if (!created)
        {
            MessageBox.Show("VSDeck is already running.", "VSDeck");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            AppStorage.Log(args.Exception.ToString());
            MessageBox.Show("VSDeck encountered an error:\n" + args.Exception.Message +
                "\n\n" + (AppStorage.LoggingWarning ?? "See " + AppStorage.LogPath),
                "VSDeck error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            args.Handled = true;
        };
        try
        {
            MainWindow = new DeckWindow(AppStorage.Load());
            MainWindow.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            AppStorage.Log(ex.ToString());
            MessageBox.Show("Unable to load VSDeck settings:\n" + ex.Message +
                (AppStorage.LoggingWarning is { } warning ? "\n\n" + warning : ""), "VSDeck error");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
