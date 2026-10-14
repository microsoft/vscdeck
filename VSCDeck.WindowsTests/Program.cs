using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VSCDeck.App;
using VSCDeck.Core;

namespace VSCDeck.WindowsTests;

internal static class Program
{
    private static int checks;
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X; public int Y; }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--logging-only")) return RunLoggingTests();
        if (args.Contains("--ssh-only")) return RunSshTests(args);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var baseline = NativeWindows.Discover().Select(w => w.Handle).ToHashSet();
        var originalSettings = File.Exists(AppStorage.SettingsPath) ? File.ReadAllText(AppStorage.SettingsPath) : null;
        var testWindows = new List<Window>();
        try
        {
            Check(new Settings().Hotkeys == string.Join(", ", "1234567890".Select(key => $"Ctrl+Shift+{key}")),
                "Default shortcuts use Ctrl+Shift in left-to-right numeric order");
            Check(new Settings().SnapToScreenEdges, "Screen-edge snapping is enabled by default");
            var sshConfig = Path.Combine(Path.GetTempPath(), "vscdeck-ssh-" + Guid.NewGuid() + ".config");
            File.WriteAllText(sshConfig, "Host devbox backup\n  HostName example\nHost *.internal !blocked\n");
            try
            {
                Check(RemoteSourceDiscovery.ReadSshHosts(sshConfig).SetEquals(["devbox", "backup"]),
                    "SSH discovery accepts concrete aliases and excludes patterns");
            }
            finally { File.Delete(sshConfig); }
            var discoveryWindow = new CodeWindow(0, 0,
                "sample [Dev Container: Sample @ desktop-linux] - Visual Studio Code",
                "sample [Dev Container: Sample @ desktop-linux]", null);
            var containerJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    Config = new
                    {
                        User = "",
                        Labels = new Dictionary<string, string>
                        {
                            ["devcontainer.local_folder"] = @"c:\work\sample",
                            ["devcontainer.metadata"] = """[{"remoteUser":"vscode"}]"""
                        }
                    }
                }
            });
            var parsedSources = RemoteSourceDiscovery.ParseContainers(
                containerJson, "desktop-linux", null, [discoveryWindow]);
            Check(parsedSources is [{ Project: "sample", Workspace: @"c:\work\sample",
                    DockerContext: "desktop-linux", SshHost: null,
                    SessionRoot: "/home/vscode/.copilot/session-state" }],
                "Container labels produce a matching remote source and session root");
            var monitorBounds = new NativeWindows.PixelRect(-1920, -200, 0, 880);
            Check(NativeWindows.CalculateSnapOffset(
                new NativeWindows.PixelRect(-1913, -192, -1513, 108), monitorBounds, 8) == new NativeWindows.PixelOffset(-7, -8),
                "Window snaps to the top and left edges of a monitor with negative coordinates");
            Check(NativeWindows.CalculateSnapOffset(
                new NativeWindows.PixelRect(-407, 572, -7, 872), monitorBounds, 8) == new NativeWindows.PixelOffset(7, 8),
                "Window snaps to the bottom and right edges of a secondary monitor");
            Check(NativeWindows.CalculateSnapOffset(
                new NativeWindows.PixelRect(-1911, -191, -1511, 109), monitorBounds, 8) == new NativeWindows.PixelOffset(0, 0),
                "Window does not snap beyond the eight-pixel threshold");
            var saved = AppStorage.Load();
            saved.ButtonSize = 30;
            AppStorage.Save(saved);
            var migrated = AppStorage.Load();
            Check(migrated.ButtonSize == 32 && AppStorage.Load().ButtonSize == 32,
                "Legacy 30-pixel size migrates persistently to 32");
            Check(migrated.Order.SequenceEqual(saved.Order) && migrated.Hotkeys == saved.Hotkeys &&
                migrated.RemoteSources.Select(s => s.Identity).SequenceEqual(saved.RemoteSources.Select(s => s.Identity)) &&
                migrated.SnapToScreenEdges == saved.SnapToScreenEdges &&
                migrated.Left == saved.Left && migrated.Top == saved.Top,
                "Size migration preserves order, hotkeys, sources, snapping, and position");
            var discovery = RemoteSourceDiscovery.Discover().GetAwaiter().GetResult();
            foreach (var warning in discovery.Warnings) Console.WriteLine(warning);
            var devContainerWindows = NativeWindows.Discover()
                .Where(window => window.Project.Contains("[Dev Container:", StringComparison.OrdinalIgnoreCase)).ToList();
            Check(discovery.Warnings.Count == 0 && discovery.Sources.Count == devContainerWindows.Count &&
                discovery.Sources.All(source => devContainerWindows.Any(source.Matches)),
                "Fresh discovery configures every open Docker/SSH dev-container window");
            var configured = (args.Contains("--fresh-discovery")
                    ? discovery.Sources
                    : AppStorage.Load().RemoteSources)
                .Where(s => !args.Contains("--local-only") || s.SshHost is null).ToList();
            if (configured.Count > 0)
            {
                using var monitor = new CopilotMonitor(configured);
                var remote = monitor.Scan();
                PumpUntil(() =>
                {
                    remote = monitor.Scan();
                    return remote.AvailableWorkspaces.Count == configured.Count || remote.Warnings.Count > 0;
                }, 90000);
                foreach (var warning in remote.Warnings) Console.WriteLine(warning);
                Check(remote.Warnings.Count == 0 && remote.AvailableWorkspaces.Count == configured.Count,
                    "All configured containers provide readable remote telemetry");
                Check(configured.All(s => remote.Windows.Any(w => w.Workspace == s.Identity)),
                    "Every configured remote workspace maps to its existing VS Code window");
                foreach (var source in configured)
                {
                    var status = WindowStatus.Aggregate(remote.Sessions.Where(s => s.Workspace == source.Identity), sourceAvailable: true);
                    Console.WriteLine($"Remote {source.Project}: {status.Activity}, {status.Sessions.Count} live sessions");
                }
                var remoteAgain = monitor.Scan();
                Check(remoteAgain.Sessions.Count == remote.Sessions.Count, "Cached remote poll preserves every session");
            }
            var target = new Window { Title = "VSC Deck isolated activation test", Width = 300, Height = 150, Left = 60, Top = 60, Topmost = true };
            testWindows.Add(target);
            target.Show();
            target.Activate();
            if (NativeWindows.GetForegroundWindow() != new WindowInteropHelper(target).Handle)
            {
                GetCursorPos(out var cursor);
                var point = target.PointToScreen(new Point(30, 45));
                SetCursorPos((int)point.X, (int)point.Y);
                mouse_event(2, 0, 0, 0, 0);
                mouse_event(4, 0, 0, 0, 0);
                PumpFor(120);
                SetCursorPos(cursor.X, cursor.Y);
            }
            PumpUntil(() => NativeWindows.GetForegroundWindow() == new WindowInteropHelper(target).Handle);
            var handle = new WindowInteropHelper(target).Handle;
            Check(NativeWindows.Activate(handle) is null, "Foreground activation request");
            PumpUntil(() => NativeWindows.IsZoomed(handle));
            Check(NativeWindows.IsZoomed(handle), "Foreground hotkey action maximizes test window");
            Check(NativeWindows.Activate(handle) is null, "Second foreground activation request");
            PumpUntil(() => !NativeWindows.IsZoomed(handle));
            Check(!NativeWindows.IsZoomed(handle), "Repeated foreground action restores test window");
            var targetBounds = NativeWindows.GetWindowBounds(handle);
            var screenBounds = NativeWindows.GetMonitorBounds(handle);
            var targetWidth = targetBounds.Right - targetBounds.Left;
            var targetHeight = targetBounds.Bottom - targetBounds.Top;
            Check(SetWindowPos(handle, 0, screenBounds.Left + 3, screenBounds.Top + 3,
                targetWidth, targetHeight, 0x0004 | 0x0010),
                "Test window positioned near top-left monitor edges");
            Check(DeckWindow.ApplyScreenEdgeSnap(handle, false) == default &&
                NativeWindows.GetWindowBounds(handle) is var unsnapped &&
                unsnapped.Left == screenBounds.Left + 3 && unsnapped.Top == screenBounds.Top + 3,
                "A real near-edge window remains unsnapped when the setting is disabled");
            Check(DeckWindow.ApplyScreenEdgeSnap(handle, true) == new NativeWindows.PixelOffset(-3, -3) &&
                NativeWindows.GetWindowBounds(handle) is var topLeft &&
                topLeft.Left == screenBounds.Left && topLeft.Top == screenBounds.Top,
                "A real window snaps exactly to top-left monitor edges");
            Check(SetWindowPos(handle, 0, screenBounds.Right - targetWidth - 3, screenBounds.Bottom - targetHeight - 3,
                targetWidth, targetHeight, 0x0004 | 0x0010),
                "Test window positioned near bottom-right monitor edges");
            Check(DeckWindow.ApplyScreenEdgeSnap(handle, true) == new NativeWindows.PixelOffset(3, 3) &&
                NativeWindows.GetWindowBounds(handle) is var bottomRight &&
                bottomRight.Right == screenBounds.Right && bottomRight.Bottom == screenBounds.Bottom,
                "A real window snaps exactly to bottom-right monitor edges");

            var settings = new Settings { ButtonSize = 64, Left = SystemParameters.PrimaryScreenWidth - 50, Top = 180,
                RemoteSources = configured };
            var deck = new DeckWindow(settings);
            testWindows.Add(deck);
            deck.Show();
            PumpUntil(() => TileButtons(deck).Count > 0, 30000);
            var tileButtons = TileButtons(deck);
            Check(tileButtons.Count == baseline.Count && tileButtons.Count > 0, "Every existing VS Code top-level window discovered");
            Check(deck.Topmost, "Panel is always on top");
            Check(deck.Left + deck.ActualWidth <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth,
                "Panel remains on screen after expanding to discovered windows");
            var dpi = VisualTreeHelper.GetDpi(deck);
            var compactToggle = Descendants<Button>(deck).Single(b => Equals(b.Content, "↙"));
            var settingsButton = Descendants<Button>(deck).Single(b =>
                System.Windows.Automation.AutomationProperties.GetName(b) == "Settings");
            var closeButton = Descendants<Button>(deck).Single(b =>
                System.Windows.Automation.AutomationProperties.GetName(b) == "Close VSC Deck");
            Check(compactToggle.IsVisible && compactToggle.PointToScreen(new Point()).X <
                settingsButton.PointToScreen(new Point()).X,
                "Large view shows the compact toggle to the left of Settings");
            Check(new[] { compactToggle, settingsButton, closeButton }.All(b =>
                    Math.Abs(b.ActualWidth - b.ActualHeight) < .1 &&
                    Math.Abs(b.ActualWidth - compactToggle.ActualWidth) < .1 &&
                    b.BorderThickness == new Thickness(0) &&
                    b.Template.FindName("CaptionSurface", b) is Border { Background: SolidColorBrush brush } &&
                    brush.Color.A == 0),
                "Large title-bar actions are equal square, flat Windows-style caption buttons");
            Check(new FrameworkElement[]
                {
                    compactToggle,
                    settingsButton,
                    closeButton,
                    Descendants<TextBlock>(deck).Single(t => t.Text == "VSC Deck  ::")
                }.All(element => element.ToolTip is null),
                "Large title-bar controls have no tooltips");
            Check(tileButtons.All(b => Math.Abs(b.ActualWidth * dpi.DpiScaleX - 64) < .1 &&
                Math.Abs(b.ActualHeight * dpi.DpiScaleY - 64) < .1), "All large buttons are exactly 64 x 64 physical pixels");
            Check(tileButtons.Any(b => ((TextBlock)b.Content).Text.Contains("Running")), "Real live agent session displays Running");
            Check(tileButtons.All(b => b.Background is LinearGradientBrush { GradientStops.Count: 4 } &&
                b.Template.FindName("GlassSheen", b) is Border { CornerRadius.TopLeft: > 0 }),
                "Large buttons have rounded glass surfaces, state gradients, and a separate sheen");
            GetCursorPos(out var hoverCursor);
            try
            {
                var point = tileButtons[0].PointToScreen(new Point(tileButtons[0].ActualWidth / 2, tileButtons[0].ActualHeight / 2));
                SetCursorPos((int)point.X, (int)point.Y);
                PumpFor(120);
                Check(tileButtons[0].IsMouseOver && tileButtons[0].Background is LinearGradientBrush &&
                    tileButtons[0].Template.FindName("HoverLight", tileButtons[0]) is Border { Opacity: 1 },
                    "Hover adds a glass highlight without replacing the status-colored surface");
            }
            finally { SetCursorPos(hoverCursor.X, hoverCursor.Y); }
            Check(tileButtons.All(b => VisualTreeHelper.GetParent(b) is StackPanel p && p.Children.OfType<Border>().Any()), "Each button has a separate draggable grip");
            Check(tileButtons.Select(b => (StackPanel)VisualTreeHelper.GetParent(b))
                .SelectMany(p => p.Children.OfType<Border>()).All(grip => grip.ToolTip is null),
                "Large reorder grips have no tooltips");

            using (var lockedLog = new FileStream(AppStorage.LogPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None))
            {
                AppStorage.Log("Test diagnostic while the log is intentionally locked.");
                Check(AppStorage.LoggingWarning is not null, "An exclusively locked log exposes an explicit warning");
                PumpUntil(() => Descendants<TextBlock>(deck).Any(t => t.Text.Contains("Logging warning")));
                using var heartbeat = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppStorage.DirectoryPath, "status.json")));
                Check(heartbeat.RootElement.GetProperty("Warnings").EnumerateArray().Any(w =>
                    w.GetString()?.Contains("Cannot write log") == true),
                    "Log failures are visible in the panel and diagnostic snapshot");
                var before = heartbeat.RootElement.GetProperty("UpdatedAt").GetDateTimeOffset();
                PumpUntil(() =>
                {
                    using var after = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppStorage.DirectoryPath, "status.json")));
                    return after.RootElement.GetProperty("UpdatedAt").GetDateTimeOffset() > before;
                });
                Check(tileButtons.Any(b => ((TextBlock)b.Content).Text.Contains("Running")),
                    "A locked log does not stall status updates or hide active sessions");
            }
            AppStorage.Log("Log-lock regression test completed; normal logging restored.");
            PumpUntil(() => !Descendants<TextBlock>(deck).Any(t => t.Text.Contains("Logging warning")));
            Check(AppStorage.LoggingWarning is null, "Logging warning clears after access recovers");

            PumpUntil(() => File.Exists(Path.Combine(AppStorage.DirectoryPath, "status.json")));
            using (var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppStorage.DirectoryPath, "status.json"))))
                Check(status.RootElement.GetProperty("Windows").EnumerateArray().Any(w =>
                    w.GetProperty("Workspace").ValueKind == JsonValueKind.String &&
                    w.GetProperty("Workspace").GetString() == @"c:\bren\juno\juno58_vsdeck" &&
                    w.GetProperty("Registered").GetBoolean()), "Current workspace has a registered global hotkey");

            if (tileButtons.Count >= 2)
            {
                var firstName = System.Windows.Automation.AutomationProperties.GetName(tileButtons[0]);
                var targetName = System.Windows.Automation.AutomationProperties.GetName(tileButtons[1]);
                var parent = (StackPanel)VisualTreeHelper.GetParent(tileButtons[0]);
                var grip = parent.Children.OfType<Border>().Single();
                var from = grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2));
                var to = tileButtons[1].PointToScreen(new Point(tileButtons[1].ActualWidth / 2, tileButtons[1].ActualHeight / 2));
                GetCursorPos(out var cursor);
                try
                {
                    SetCursorPos((int)from.X, (int)from.Y);
                    PumpFor(100);
                    mouse_event(2, 0, 0, 0, 0);
                    var moveThread = new Thread(() =>
                    {
                        Thread.Sleep(150);
                        SetCursorPos((int)(from.X + SystemParameters.MinimumHorizontalDragDistance + 2), (int)from.Y);
                        Thread.Sleep(250);
                        for (var step = 1; step <= 15; step++)
                        {
                            SetCursorPos((int)(from.X + (to.X - from.X) * step / 15),
                                (int)(from.Y + (to.Y - from.Y) * step / 15));
                            Thread.Sleep(30);
                        }
                        mouse_event(4, 0, 0, 0, 0);
                    });
                    moveThread.Start();
                    PumpFor(1300);
                    moveThread.Join();
                    tileButtons = TileButtons(deck);
                    Check(System.Windows.Automation.AutomationProperties.GetName(tileButtons[0]) == targetName &&
                        System.Windows.Automation.AutomationProperties.GetName(tileButtons[1]) == firstName,
                        "Real mouse grip drag reorders buttons");
                    var loaded = AppStorage.Load();
                    Check(loaded.Order.SequenceEqual(settings.Order) && loaded.Order.Count > 0, "Drag ordering persists to disk");
                }
                finally { mouse_event(4, 0, 0, 0, 0); SetCursorPos(cursor.X, cursor.Y); }
            }
            if (args.Length > 0) Screenshot(deck, Path.Combine(args[0], "panel-64.png"));
            deck.Close();

            var compactSettings = new Settings { ButtonSize = 32, Left = 200, Top = 180, Order = settings.Order,
                RemoteSources = configured };
            var compact = new DeckWindow(compactSettings);
            testWindows.Add(compact);
            compact.Show();
            PumpUntil(() => TileButtons(compact).Count > 0, 30000);
            var compactButtons = TileButtons(compact);
            dpi = VisualTreeHelper.GetDpi(compact);
            Check(compactButtons.All(b => Math.Abs(b.ActualWidth * dpi.DpiScaleX - 32) < .1 &&
                Math.Abs(b.ActualHeight * dpi.DpiScaleY - 32) < .1), "All compact buttons are exactly 32 x 32 physical pixels");
            Check(compactButtons.All(b => b.Background is LinearGradientBrush &&
                b.Template.FindName("GlassSheen", b) is Border),
                "Compact buttons retain the same glass finish within their exact pixel bounds");
            Check(compactButtons.All(b => VisualTreeHelper.GetParent(b) is StackPanel p &&
                !p.Children.OfType<Border>().Any()), "Compact buttons omit reorder grips");
            Check(Descendants<TextBlock>(compact).Where(t => t.Text == "VSC Deck  ::")
                    .All(t => !t.IsVisible) &&
                Descendants<Button>(compact).Where(b =>
                    System.Windows.Automation.AutomationProperties.GetName(b) is "Settings" or "Close VSC Deck")
                    .All(b => !b.IsVisible),
                "Compact view hides the title bar, settings button, and close button");
            var largeToggle = Descendants<Button>(compact).Single(b => Equals(b.Content, "↗"));
            var moveGrip = Descendants<Border>(compact).Single(b =>
                System.Windows.Automation.AutomationProperties.GetName(b) == "Move compact view");
            Check(largeToggle.ToolTip is null && moveGrip.ToolTip is null,
                "Compact toggle and drag handle have no tooltips");
            Check(largeToggle.BorderThickness == new Thickness(0) &&
                largeToggle.Template.FindName("CaptionSurface", largeToggle) is Border
                    { Background: SolidColorBrush brush } && brush.Color.A == 0,
                "Compact expand toggle uses the flat transparent caption style");
            Check(Math.Abs(moveGrip.ActualWidth * dpi.DpiScaleX - 16) < .1 &&
                Math.Abs(moveGrip.ActualHeight - compact.ActualHeight) < .1,
                "Compact drag handle is 16 physical pixels wide and spans the full window height");
            if (settings.Order.Count > 0)
                Check(System.Windows.Automation.AutomationProperties.GetName(compactButtons[0]) ==
                    System.Windows.Automation.AutomationProperties.GetName(tileButtons[0]), "Restart reconstructs reordered window positions");
            if (args.Length > 0) Screenshot(compact, Path.Combine(args[0], "panel-32.png"));
            largeToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => compactSettings.ButtonSize == 64 && TileButtons(compact).All(b =>
                Math.Abs(b.ActualWidth * dpi.DpiScaleX - 64) < .1));
            Check(Descendants<TextBlock>(compact).Any(t => t.Text == "VSC Deck  ::"),
                "Compact expand button switches to large view");
            Descendants<Button>(compact).Single(b => Equals(b.Content, "↙"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => compactSettings.ButtonSize == 32 && TileButtons(compact).All(b =>
                Math.Abs(b.ActualWidth * dpi.DpiScaleX - 32) < .1));
            Check(!Descendants<TextBlock>(compact).Single(t => t.Text == "VSC Deck  ::").IsVisible,
                "Large compact button switches back to compact view");
            compact.Close();

            Check(NativeWindows.RegisterHotKey(handle, 901, 6 | 0x4000, 49), "Isolated Ctrl+Shift conflict hotkey registered");
            var conflict = new DeckWindow(new Settings { Left = 200, Top = 180, RemoteSources = configured });
            testWindows.Add(conflict);
            try
            {
                conflict.Show();
                PumpUntil(() => TileButtons(conflict).Count > 0, 30000);
                Check(Descendants<TextBlock>(conflict).Any(t => t.Text.Contains("hotkey warning")),
                    "Global hotkey conflict is visibly reported");
            }
            finally
            {
                conflict.Close();
                NativeWindows.UnregisterHotKey(handle, 901);
            }
            var dialog = new SettingsWindow(new Settings { ButtonSize = 64 });
            testWindows.Add(dialog);
            dialog.Show();
            var viewOptions = Descendants<ComboBox>(dialog).Single();
            Check(viewOptions.Items.Cast<string>().SequenceEqual(["Large", "Compact"]),
                "Settings exposes large and compact view options");
            viewOptions.SelectedItem = "Compact";
            var snapOption = Descendants<CheckBox>(dialog).Single(c => Equals(c.Content, "Snap to screen edges"));
            Check(snapOption.IsChecked == true, "Settings enables screen-edge snapping by default");
            snapOption.IsChecked = false;
            Check(Descendants<Button>(dialog).Any(b => Equals(b.Content, "Discover Docker/SSH sources")),
                "Settings offers Docker and SSH source discovery");
            var save = Descendants<Button>(dialog).Single(b => Equals(b.Content, "Save"));
            // ShowDialog is required for DialogResult; validate available settings controls without closing VS Code.
            Check(Equals(Descendants<ComboBox>(dialog).Single().SelectedItem, "Compact") && save.IsDefault,
                "Settings offers view and screen-edge snapping selection with a save action");
            dialog.Close();
            Check(baseline.SetEquals(NativeWindows.Discover().Select(w => w.Handle)), "All pre-existing VS Code windows remain open and untouched");
            Console.WriteLine($"All {checks} Windows checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            foreach (var window in testWindows.Where(w => w.IsVisible)) window.Close();
            if (originalSettings is not null) File.WriteAllText(AppStorage.SettingsPath, originalSettings);
            else if (File.Exists(AppStorage.SettingsPath)) File.Delete(AppStorage.SettingsPath);
            app.Shutdown();
        }
    }

    private static List<Button> TileButtons(Window window) =>
        Descendants<Button>(window).Where(b => b.Content is TextBlock).ToList();

    private static int RunLoggingTests()
    {
        var path = Path.Combine(Path.GetTempPath(), "vscdeck-log-test-" + Guid.NewGuid() + ".log");
        try
        {
            var collisions = 0;
            Parallel.For(0, 1000, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            {
                try { File.AppendAllText(path, $"{i} {new string('x', 2048)}\n"); }
                catch (IOException) { Interlocked.Increment(ref collisions); }
            });
            Console.WriteLine($"Original unsynchronized logging reproduced {collisions} sharing failures.");
            File.Delete(path);
            var logger = new LogWriter(path);
            Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 16 },
                i => logger.Write($"parallel-entry-{i}"));
            var lines = File.ReadAllLines(path);
            Check(logger.Warning is null, "Concurrent log writers have no sharing failures");
            Check(lines.Length == 2000 && lines.Select(line => line[(line.IndexOf(' ') + 1)..]).ToHashSet().Count == 2000,
                "All 2000 concurrent messages are written exactly once without interleaving");
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                logger.Write("reader-open");
                Check(logger.Warning is null, "Logging works while a compatible log reader is open");
            }
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                logger.Write("pending diagnostic");
                Check(logger.Warning?.Contains("pending diagnostic") == true && logger.Warning.Contains(path),
                    "Exclusive lock is reported with the path and original diagnostic, not thrown");
            }
            logger.Write("recovered");
            var contents = File.ReadAllText(path);
            Check(logger.Warning is null && contents.Contains("Logging recovered after:") &&
                contents.Contains("pending diagnostic") && contents.Contains("recovered"),
                "Logging recovers and records the previously blocked diagnostic");
            var denied = new LogWriter(Path.GetTempPath());
            denied.Write("unwritable-path");
            Check(denied.Warning?.Contains("unwritable-path") == true,
                "An unwritable log target reports an explicit error without masking the diagnostic");
            Console.WriteLine($"All {checks} logging checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static int RunSshTests(string[] args)
    {
        var index = Array.IndexOf(args, "--ssh-only");
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine("--ssh-only requires a configured SSH host.");
            return 1;
        }
        try
        {
            Check(SshCommandChannel.ReconnectDelay(1) == TimeSpan.FromSeconds(5) &&
                SshCommandChannel.ReconnectDelay(2) == TimeSpan.FromSeconds(10) &&
                SshCommandChannel.ReconnectDelay(3) == TimeSpan.FromSeconds(20) &&
                SshCommandChannel.ReconnectDelay(20) == TimeSpan.FromSeconds(30),
                "Shared SSH reconnect backoff grows from 5 to at most 30 seconds");
            var channel = new SshCommandChannel(args[index + 1]);
            int? pid;
            using (channel)
            {
                var first = channel.Run(["python3", "-c", "print('first')"]).GetAwaiter().GetResult();
                pid = channel.ConnectionProcessId;
                Check(first.Trim() == "first" && pid is > 0, "Persistent SSH helper connects and returns a command response");
                var replies = Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
                    channel.Run(["python3", "-c", $"print('reply-{i}')"]))).GetAwaiter().GetResult();
                Check(replies.Select(s => s.Trim()).SequenceEqual(Enumerable.Range(0, 6).Select(i => $"reply-{i}")),
                    "Parallel callers receive their own serialized responses");
                Check(channel.ConnectionProcessId == pid, "All parallel requests reuse one SSH client process");
                var failed = false;
                try
                {
                    channel.Run(["python3", "-c", "import sys; print('intentional failure', file=sys.stderr); sys.exit(7)"])
                        .GetAwaiter().GetResult();
                }
                catch (IOException ex) { failed = ex.Message.Contains("exited 7") && ex.Message.Contains("intentional failure"); }
                Check(failed, "Remote command errors preserve their exit code and diagnostic");
                var recovered = channel.Run(["python3", "-c", "print('recovered')"]).GetAwaiter().GetResult();
                Check(recovered.Trim() == "recovered" && channel.ConnectionProcessId == pid,
                    "A command error does not tear down the healthy SSH connection");
            }
            var exited = false;
            try { using var process = Process.GetProcessById(pid!.Value); exited = process.HasExited; }
            catch (ArgumentException) { exited = true; }
            Check(exited && channel.ConnectionProcessId is null, "Disposal closes only the SSH client owned by the test");
            Console.WriteLine($"All {checks} SSH checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void PumpUntil(Func<bool> condition, int timeout = 4000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeout) PumpFor(40);
        if (!condition()) throw new TimeoutException("Timed out waiting for Windows UI.");
    }

    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        checks++;
        Console.WriteLine("PASS: " + message);
    }

    private static void Screenshot(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
