using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
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
            var defaultSettings = new Settings();
            Check(defaultSettings.Hotkeys == string.Join(", ", "1234567890".Select(key => $"Ctrl+Shift+{key}")),
                "Default shortcuts use Ctrl+Shift in left-to-right numeric order");
            Check(defaultSettings.SnapToScreenEdges, "Screen-edge snapping is enabled by default");
            Check(!defaultSettings.UseNotificationArea && !defaultSettings.StartWithWindows,
                "Notification-area and Windows-startup options are disabled by default");
            Check(WorkspaceColorCustomization.Parse("""
                {
                    // VS Code workspace settings support JSON with comments.
                    "workbench.colorCustomizations": {
                        "statusBar.debuggingBackground": "#ffff33",
                    },
                }
                """) == "#FFFFFF33" &&
                WorkspaceColorCustomization.Parse("""
                { "settings": { "workbench.colorCustomizations": {
                    "statusBar.debuggingBackground": "#1a2b"
                } } }
                """) == "#BB11AA22",
                "Workspace color parsing supports JSONC, folder settings, workspace settings, and VS Code RGBA colors");
            Check(WorkspaceColorCustomization.Parse("""{"editor.fontSize": 14}""") is null &&
                DeckWindow.ColorStripeBrush(null) is SolidColorBrush { Color.A: 0 },
                "A workspace without an explicit debugging color uses a transparent stripe");
            var startupTestPath = @"Software\VSCDeck.Tests." + Guid.NewGuid().ToString("N");
            try
            {
                using var startupKey = Registry.CurrentUser.CreateSubKey(startupTestPath, true);
                const string startupValue = "VSCDeck-Test";
                const string executablePath = @"C:\Program Files\VSC Deck\VSCDeck.exe";
                WindowsStartup.SetEnabled(startupKey, startupValue, true, executablePath);
                Check(Equals(startupKey.GetValue(startupValue), $"\"{executablePath}\""),
                    "Windows startup registration quotes the executable path");
                WindowsStartup.SetEnabled(startupKey, startupValue, false, executablePath);
                Check(startupKey.GetValue(startupValue) is null,
                    "Disabling Windows startup removes its current-user registration");
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(startupTestPath, false); }
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
            Check(DeckWindow.CalculateInsertionIndex([10, 30, 50], 5) == 0 &&
                DeckWindow.CalculateInsertionIndex([10, 30, 50], 10) == 1 &&
                DeckWindow.CalculateInsertionIndex([10, 30, 50], 40) == 2 &&
                DeckWindow.CalculateInsertionIndex([10, 30, 50], 55) == 3,
                "Reorder insertion positions cover the left edge, gaps, and right edge");
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
            saved.UseNotificationArea = true;
            saved.StartWithWindows = true;
            AppStorage.Save(saved);
            var migrated = AppStorage.Load();
            Check(migrated.ButtonSize == 32 && AppStorage.Load().ButtonSize == 32,
                "Legacy 30-pixel size migrates persistently to 32");
            Check(migrated.Order.SequenceEqual(saved.Order) && migrated.Hotkeys == saved.Hotkeys &&
                migrated.RemoteSources.Select(s => s.Identity).SequenceEqual(saved.RemoteSources.Select(s => s.Identity)) &&
                migrated.SnapToScreenEdges == saved.SnapToScreenEdges &&
                migrated.UseNotificationArea == saved.UseNotificationArea &&
                migrated.StartWithWindows == saved.StartWithWindows &&
                migrated.Left == saved.Left && migrated.Top == saved.Top,
                "Size migration preserves order, hotkeys, sources, startup, tray, snapping, and position");
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
                var juno39 = configured.FirstOrDefault(source =>
                    source.Workspace.Contains("juno39_mcma", StringComparison.OrdinalIgnoreCase));
                if (juno39 is not null)
                    Check(remote.Windows.Single(juno39.Matches).DebuggingBackground == "#FFFFFF33",
                        "Juno 39 resolves its yellow debugging status-bar customization");
                foreach (var source in configured)
                {
                    var status = WindowStatus.Aggregate(remote.Sessions.Where(s => s.Workspace == source.Identity), sourceAvailable: true);
                    Console.WriteLine($"Remote {source.Project}: {status.Activity}, {status.Sessions.Count} live sessions");
                }
                var remoteAgain = monitor.Scan();
                Check(remoteAgain.Sessions.Count == remote.Sessions.Count, "Cached remote poll preserves every session");
            }
            var chatTarget = NativeWindows.Discover().First();
            Check(NativeWindows.Activate(chatTarget.Handle) is null, "Real VS Code activation for Copilot Chat focus");
            PumpUntil(() => NativeWindows.GetForegroundWindow() == chatTarget.Handle);
            Check(NativeWindows.OpenCopilotChat(chatTarget.Handle) is null, "Real VS Code Chat view open request");
            string? chatFocusWarning = null;
            PumpUntil(() =>
            {
                chatFocusWarning = NativeWindows.FocusCopilotChatInput(chatTarget.Handle);
                return chatFocusWarning is null;
            }, 3000);
            var focusedChatInput = AutomationElement.FocusedElement;
            var chatEditor = TreeWalker.ControlViewWalker.GetParent(focusedChatInput);
            var chatSession = chatEditor is null ? null : TreeWalker.ControlViewWalker.GetParent(chatEditor);
            Check(chatFocusWarning is null &&
                focusedChatInput.Current.ControlType == ControlType.Edit &&
                focusedChatInput.Current.ClassName == "native-edit-context" &&
                chatSession?.Current.ClassName.StartsWith("interactive-session",
                    StringComparison.Ordinal) == true,
                "Real VS Code Copilot Chat text box receives keyboard focus");
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
                UseNotificationArea = true,
                RemoteSources = configured };
            var deck = new DeckWindow(settings);
            testWindows.Add(deck);
            deck.Show();
            PumpUntil(() => TileButtons(deck).Count > 0, 30000);
            var tileButtons = TileButtons(deck);
            var iconResource = Application.GetResourceStream(
                new Uri("pack://application:,,,/VSCDeck;component/Assets/VSCDeck.ico"));
            using (var iconReader = new BinaryReader(iconResource.Stream, System.Text.Encoding.UTF8, true))
            {
                var reserved = iconReader.ReadUInt16();
                var type = iconReader.ReadUInt16();
                var frameCount = iconReader.ReadUInt16();
                var frameLength = 0;
                var frameOffset = 0;
                for (var index = 0; index < frameCount; index++)
                {
                    var width = iconReader.ReadByte();
                    iconReader.ReadByte();
                    iconReader.ReadBytes(6);
                    var length = iconReader.ReadInt32();
                    var offset = iconReader.ReadInt32();
                    if (width == 32) (frameLength, frameOffset) = (length, offset);
                }
                Check(deck.Icon is not null && reserved == 0 && type == 1 && frameCount == 9 &&
                    frameLength > 0,
                    "Window and executable use the nine-frame VSC Deck taskbar icon");
                iconReader.BaseStream.Position = frameOffset;
                using var frameStream = new MemoryStream(iconReader.ReadBytes(frameLength));
                var iconFrame = new PngBitmapDecoder(frameStream, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad).Frames[0];
                Check(iconFrame.Format.BitsPerPixel == 32 &&
                    IconAlpha(iconFrame, .27, .5) > 180 &&
                    IconAlpha(iconFrame, .73, .5) < 40 &&
                    IconAlpha(iconFrame, .57, .5) > 20,
                    "Taskbar icon has a solid left square and hollow right square on transparency");
            }
            static byte IconAlpha(BitmapSource source, double x, double y)
            {
                var pixel = new byte[4];
                source.CopyPixels(new Int32Rect(
                    Math.Min(source.PixelWidth - 1, (int)(source.PixelWidth * x)),
                    Math.Min(source.PixelHeight - 1, (int)(source.PixelHeight * y)), 1, 1), pixel, 4, 0);
                return pixel[3];
            }
            Check(!deck.ShowInTaskbar && deck.IsNotificationIconVisible &&
                deck.NotificationMenuItems.SequenceEqual(["Hide VSC Deck", "Settings", "Exit"]),
                "Notification-area mode replaces the taskbar button and provides panel controls");
            deck.TogglePanelVisibility();
            Check(!deck.IsVisible && deck.NotificationMenuItems.Contains("Show VSC Deck"),
                "Notification icon can hide the status panel");
            deck.TogglePanelVisibility();
            Check(deck.IsVisible && deck.NotificationMenuItems.Contains("Hide VSC Deck"),
                "Notification icon can restore the status panel");
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
            var largeStripes = tileButtons.Select(ColorStripe).ToArray();
            Check(largeStripes.All(stripe =>
                    Math.Abs(stripe.Height * dpi.DpiScaleY - 2) < .1 &&
                    Math.Abs(stripe.Margin.Top * dpi.DpiScaleY - 1) < .1),
                "Large workspace stripes are two physical pixels high with a one-pixel button gap");
            if (configured.Any(source =>
                    source.Workspace.Contains("juno39_mcma", StringComparison.OrdinalIgnoreCase)))
            {
                PumpUntil(() => largeStripes.Any(stripe => stripe.Background is SolidColorBrush
                    { Color.R: 255, Color.G: 255, Color.B: 51, Color.A: 255 }), 90000);
                Check(largeStripes.Any(stripe => stripe.Background is SolidColorBrush
                        { Color.R: 255, Color.G: 255, Color.B: 51, Color.A: 255 }),
                    "Juno 39 displays its yellow workspace-color stripe");
            }
            var runningButton = tileButtons.First(b =>
                b.Content is TextBlock label && label.Text.Contains("Running"));
            Check(runningButton is not null, "Real live agent session displays Running");
            Check(DeckWindow.RunningPulseCycleMilliseconds == 1200 &&
                runningButton!.Template?.FindName("RunningGlow", runningButton) is Border
                    { HasAnimatedProperties: true, Background: RadialGradientBrush },
                "Running button uses a prominent animated lamp glow on a 1200-millisecond cycle");
            Check(tileButtons.Where(b => b != runningButton).All(b =>
                    b.Template?.FindName("RunningGlow", b) is Border { HasAnimatedProperties: false, Opacity: 0 }),
                "Non-running lamp buttons do not pulse");
            var independentlyPulsingButtons = tileButtons.Where(button => button != runningButton).Take(2).ToArray();
            DeckWindow.SetRunningPulse(independentlyPulsingButtons[0], true);
            Check(independentlyPulsingButtons[0].Template.FindName("RunningGlow",
                    independentlyPulsingButtons[0]) is Border { HasAnimatedProperties: true } &&
                independentlyPulsingButtons[1].Template.FindName("RunningGlow",
                    independentlyPulsingButtons[1]) is Border { HasAnimatedProperties: false },
                "Starting one button's blue pulse does not start another button's animation clock");
            DeckWindow.SetRunningPulse(independentlyPulsingButtons[1], true);
            Check(independentlyPulsingButtons.All(button =>
                    button.Template.FindName("RunningGlow", button) is Border { HasAnimatedProperties: true }),
                "Each running button owns and starts its own blue pulse clock");
            foreach (var button in independentlyPulsingButtons) DeckWindow.SetRunningPulse(button, false);
            static double Luminance(Color color) => .2126 * color.R + .7152 * color.G + .0722 * color.B;
            Check(Math.Abs(Luminance(DeckWindow.IdleLampColor) - Luminance(DeckWindow.RunningLampColor)) < 2,
                "Idle green matches the perceived brightness of the running lamp's dark-blue state");
            Check(Math.Abs(Luminance(DeckWindow.QuestionLampColor) - Luminance(DeckWindow.RunningLampColor)) < 2,
                "Question amber matches the perceived brightness of the other dark lamp states");
            Check(tileButtons.Where(button => button.Content is TextBlock label &&
                    label.Text.Contains("Question")).All(button =>
                    button.Content is TextBlock { Foreground: SolidColorBrush textBrush } &&
                    textBrush.Color == Colors.White),
                "Question buttons use white text against the dark amber lamp");
            Check(DeckWindow.UpdateCompletionPending(false, VSCDeck.Core.Activity.Running, VSCDeck.Core.Activity.Idle, false) &&
                DeckWindow.UpdateCompletionPending(true, VSCDeck.Core.Activity.Idle, VSCDeck.Core.Activity.Idle, false) &&
                !DeckWindow.UpdateCompletionPending(true, VSCDeck.Core.Activity.Idle, VSCDeck.Core.Activity.Idle, true) &&
                !DeckWindow.UpdateCompletionPending(false, VSCDeck.Core.Activity.Unknown, VSCDeck.Core.Activity.Idle, false),
                "Completion blip latches only after running becomes idle and clears when its window gains focus");
            Check(DeckWindow.GetStateText(VSCDeck.Core.Activity.Idle, true, true) == "Done" &&
                DeckWindow.GetStateText(VSCDeck.Core.Activity.Idle, true, false) == "Idle" &&
                DeckWindow.GetStateText(VSCDeck.Core.Activity.Idle, false, true) == "Idle",
                "Compact completion label says Done until focus acknowledgement restores Idle");
            var idleButton = tileButtons.First(button => button != runningButton);
            DeckWindow.SetCompletionBlip(idleButton, true);
            Check(DeckWindow.BlipOnMilliseconds == 50 &&
                DeckWindow.CompletionBlipCycleMilliseconds == 2000 &&
                idleButton.Template.FindName("CompletionGlow", idleButton) is Border
                    { HasAnimatedProperties: true, Background: RadialGradientBrush completionBrush } &&
                completionBrush.GradientStops.Any(stop => stop.Color.G > stop.Color.R && stop.Color.G > stop.Color.B),
                "Completed button uses a repeating 50-millisecond green blip with a 1.95-second off state");
            DeckWindow.SetCompletionBlip(idleButton, false);
            Check(idleButton.Template.FindName("CompletionGlow", idleButton) is Border
                    { HasAnimatedProperties: false, Opacity: 0 },
                "Acknowledging completion removes the green blip");
            DeckWindow.SetQuestionBlip(idleButton, true);
            Check(DeckWindow.QuestionBlipCycleMilliseconds == 1500 &&
                idleButton.Template.FindName("QuestionGlow", idleButton) is Border
                    { HasAnimatedProperties: true, Background: RadialGradientBrush questionBrush } &&
                questionBrush.GradientStops.Any(stop => stop.Color.R > stop.Color.B && stop.Color.G > stop.Color.B),
                "Question button uses its own repeating 50-millisecond yellow blip");
            DeckWindow.SetQuestionBlip(idleButton, false);
            Check(idleButton.Template.FindName("QuestionGlow", idleButton) is Border
                    { HasAnimatedProperties: false, Opacity: 0 },
                "Resolving a question removes its yellow blip");
            var independentlyBlippingButtons = tileButtons.Where(button => button != runningButton).Take(2).ToArray();
            DeckWindow.SetCompletionBlip(independentlyBlippingButtons[0], true);
            Check(independentlyBlippingButtons[0].Template.FindName("CompletionGlow",
                    independentlyBlippingButtons[0]) is Border { HasAnimatedProperties: true } &&
                independentlyBlippingButtons[1].Template.FindName("QuestionGlow",
                    independentlyBlippingButtons[1]) is Border { HasAnimatedProperties: false },
                "Starting one button's blip does not start another button's cycle");
            DeckWindow.SetQuestionBlip(independentlyBlippingButtons[1], true);
            Check(independentlyBlippingButtons[0].Template.FindName("CompletionGlow",
                    independentlyBlippingButtons[0]) is Border { HasAnimatedProperties: true } &&
                independentlyBlippingButtons[1].Template.FindName("QuestionGlow",
                    independentlyBlippingButtons[1]) is Border { HasAnimatedProperties: true },
                "Each button starts its blip cycle independently when its event is detected");
            DeckWindow.SetCompletionBlip(independentlyBlippingButtons[0], false);
            DeckWindow.SetQuestionBlip(independentlyBlippingButtons[1], false);
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
            Check(tileButtons.All(button => ReorderGrip(button) is not null),
                "Each button has a separate draggable grip");
            Check(tileButtons.Select(ReorderGrip).All(grip => grip.ToolTip is null),
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
                var grip = ReorderGrip(tileButtons[0]);
                var from = grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2));
                var to = tileButtons[1].PointToScreen(new Point(tileButtons[1].ActualWidth / 2, tileButtons[1].ActualHeight / 2));
                var markerSeen = false;
                var originalPositionMarkerSeen = false;
                DragEventHandler observeMarker = (_, _) =>
                {
                    markerSeen |= deck.IsReorderMarkerVisible;
                    originalPositionMarkerSeen |= deck.IsReorderMarkerVisible && deck.ReorderInsertionIndex == 0;
                };
                deck.AddHandler(DragDrop.DragOverEvent, observeMarker, true);
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
                    Check(markerSeen, "Real reorder drag displays an insertion marker");
                    Check(!originalPositionMarkerSeen,
                        "Reorder drag does not display a marker for the button's existing position");
                    Check(!deck.IsReorderMarkerVisible && deck.ReorderInsertionIndex is null,
                        "Reorder insertion marker clears after drop");
                    Check(System.Windows.Automation.AutomationProperties.GetName(tileButtons[0]) == targetName &&
                        System.Windows.Automation.AutomationProperties.GetName(tileButtons[1]) == firstName,
                        "Real mouse grip drag reorders buttons");
                    var loaded = AppStorage.Load();
                    Check(loaded.Order.SequenceEqual(settings.Order) && loaded.Order.Count > 0, "Drag ordering persists to disk");
                    if (tileButtons.Count >= 3)
                    {
                        var middle = tileButtons[tileButtons.Count / 2];
                        var middleName = System.Windows.Automation.AutomationProperties.GetName(middle);
                        var middleGrip = ReorderGrip(middle);
                        var rightmost = tileButtons[^1];
                        DragWithMouse(middleGrip, rightmost.PointToScreen(
                            new Point(rightmost.ActualWidth - 1, rightmost.ActualHeight / 2)));
                        tileButtons = TileButtons(deck);
                        Check(System.Windows.Automation.AutomationProperties.GetName(tileButtons[^1]) == middleName,
                            "A middle button can be dragged to the right edge");

                        middle = tileButtons[^1];
                        middleGrip = ReorderGrip(middle);
                        var leftmost = tileButtons[0];
                        DragWithMouse(middleGrip, leftmost.PointToScreen(new Point(1, leftmost.ActualHeight / 2)));
                        tileButtons = TileButtons(deck);
                        Check(System.Windows.Automation.AutomationProperties.GetName(tileButtons[0]) == middleName,
                            "A middle button can be dragged to the left edge");
                    }
                }
                finally
                {
                    deck.RemoveHandler(DragDrop.DragOverEvent, observeMarker);
                    mouse_event(4, 0, 0, 0, 0);
                    SetCursorPos(cursor.X, cursor.Y);
                }
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
            Check(compactButtons.Select(ColorStripe).All(stripe =>
                    Math.Abs(stripe.Height * dpi.DpiScaleY - 1) < .1 &&
                    Math.Abs(stripe.Margin.Top * dpi.DpiScaleY - 1) < .1),
                "Compact workspace stripes are one physical pixel high with a one-pixel button gap");
            Check(compactButtons.All(b => VisualTreeHelper.GetParent(b) is StackPanel p &&
                p.Children.OfType<Border>().All(border =>
                    System.Windows.Automation.AutomationProperties.GetName(border) ==
                    "Workspace debugging color stripe")), "Compact buttons omit reorder grips");
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
            var notificationOption = Descendants<CheckBox>(dialog).Single(c =>
                Equals(c.Content, "Show in notification area instead of the taskbar"));
            var startupOption = Descendants<CheckBox>(dialog).Single(c =>
                Equals(c.Content, "Start VSC Deck when I sign in to Windows"));
            Check(notificationOption.IsChecked == false && startupOption.IsChecked == false,
                "Settings exposes default-off notification-area and Windows-startup options");
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

    private static Border ColorStripe(Button button) =>
        ((StackPanel)VisualTreeHelper.GetParent(button)).Children.OfType<Border>().Single(border =>
            System.Windows.Automation.AutomationProperties.GetName(border) ==
            "Workspace debugging color stripe");

    private static Border ReorderGrip(Button button) =>
        ((StackPanel)VisualTreeHelper.GetParent(button)).Children.OfType<Border>().Single(border =>
            System.Windows.Automation.AutomationProperties.GetName(border) !=
            "Workspace debugging color stripe");

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

    private static void DragWithMouse(FrameworkElement source, Point destination)
    {
        var from = source.PointToScreen(new Point(source.ActualWidth / 2, source.ActualHeight / 2));
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
                    SetCursorPos((int)(from.X + (destination.X - from.X) * step / 15),
                        (int)(from.Y + (destination.Y - from.Y) * step / 15));
                    Thread.Sleep(30);
                }
                mouse_event(4, 0, 0, 0, 0);
            });
            moveThread.Start();
            PumpFor(1300);
            moveThread.Join();
        }
        finally
        {
            mouse_event(4, 0, 0, 0, 0);
            SetCursorPos(cursor.X, cursor.Y);
        }
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
