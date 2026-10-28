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
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VSCDeck.App;
using VSCDeck.Core;
using Activity = VSCDeck.Core.Activity;

namespace VSCDeck.WindowsTests;

internal static class Program
{
    private static int checks;
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(
        nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X; public int Y; }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--pulse-only")) return RunPulseTests();
        if (args.Contains("--views-only")) return RunViewTests();
        if (args.Contains("--snapping-only")) return RunSnappingTests();
        if (args.Contains("--container-reader-only")) return RunContainerReaderTests(args);
        if (args.Contains("--discovery-only")) return RunDiscoveryTests();
        if (args.Contains("--runtime-discovery-live")) return RunLiveDiscoveryTest(args);
        if (args.Contains("--remote-lifecycle-only")) return RunRemoteLifecycleTests();
        if (args.Contains("--logging-only")) return RunLoggingTests();
        if (args.Contains("--audio-only")) return RunAudioTests();
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
                        "statusBar.background": "#ffff33",
                        "statusBar.debuggingBackground": "#000000",
                    },
                }
                """) == "#FFFFFF33" &&
                WorkspaceColorCustomization.Parse("""
                { "settings": { "workbench.colorCustomizations": {
                    "statusBar.background": "#1a2b"
                } } }
                """) == "#BB11AA22",
                "Workspace color parsing supports JSONC, folder settings, workspace settings, and VS Code RGBA colors");
            Check(WorkspaceColorCustomization.Parse("""{"editor.fontSize": 14}""") is null &&
                DeckWindow.ColorStripeBrush(null) is SolidColorBrush { Color.A: 0 },
                "A workspace without an explicit status bar color uses a transparent stripe");
            Check(WorkspaceColorCustomization.Parse("""
                {"workbench.colorCustomizations":{"statusBar.debuggingBackground":"#ffff33"}}
                """) is null,
                "A debugging-only customization does not produce a status bar stripe");
            Check(CopilotMonitor.AvailableWorkspaces(
                    [@"c:\work\local"], ["docker:devbox:/work/remote"])
                .SetEquals([@"C:\WORK\LOCAL", "docker:devbox:/work/remote"]),
                "Available workspaces include both live local IDE paths and remote sources");
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
                new NativeWindows.PixelRect(-1897, -176, -1497, 124), monitorBounds, 24) == new NativeWindows.PixelOffset(-23, -24),
                "Window snaps to the top and left edges of a monitor with negative coordinates");
            Check(NativeWindows.CalculateSnapOffset(
                new NativeWindows.PixelRect(-423, 556, -23, 856), monitorBounds, 24) == new NativeWindows.PixelOffset(23, 24),
                "Window snaps to the bottom and right edges of a secondary monitor");
            Check(NativeWindows.CalculateSnapOffset(
                new NativeWindows.PixelRect(-1895, -175, -1495, 125), monitorBounds, 24) == new NativeWindows.PixelOffset(0, 0),
                "Window does not snap beyond the 24-pixel threshold");
            var saved = AppStorage.Load();
            saved.ButtonSize = 30;
            saved.Order = ["first", "second", "third"];
            saved.RelativeOrder = [];
            saved.UseNotificationArea = true;
            saved.StartWithWindows = true;
            AppStorage.Save(saved);
            var migrated = AppStorage.Load();
            Check(migrated.ButtonSize == 32 && AppStorage.Load().ButtonSize == 32,
                "Legacy 30-pixel size migrates persistently to 32");
            Check(migrated.Order.SequenceEqual(saved.Order) && migrated.Hotkeys == saved.Hotkeys &&
                migrated.RelativeOrder.SequenceEqual(Identity.RelationsFor(saved.Order), RelativeOrder.Comparer) &&
                migrated.RemoteSources.Select(s => s.Identity).SequenceEqual(saved.RemoteSources.Select(s => s.Identity)) &&
                migrated.SnapToScreenEdges == saved.SnapToScreenEdges &&
                migrated.UseNotificationArea == saved.UseNotificationArea &&
                migrated.StartWithWindows == saved.StartWithWindows &&
                migrated.Left == saved.Left && migrated.Top == saved.Top,
                "Settings migration preserves order, hotkeys, sources, startup, tray, snapping, and position");
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
                    Check(remote.Windows.Single(juno39.Matches).StatusBarBackground == "#FFFFFF33",
                        "Juno 39 resolves its yellow status-bar customization");
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
            CheckScreenEdgeSnapping(handle);

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
            var compactToggle = Descendants<Button>(deck).Single(b => Equals(b.Content, "▲"));
            Check(compactToggle.IsVisible && compactToggle.IsEnabled &&
                !Descendants<Button>(deck).Single(b => Equals(b.Content, "▼")).IsEnabled,
                "Large view shows both arrows and disables the larger-view button");
            Check(!Descendants<TextBlock>(deck).Any(t => t.Text == "VSC Deck  ::") &&
                !Descendants<Button>(deck).Any(b =>
                    System.Windows.Automation.AutomationProperties.GetName(b) is "Settings" or "Close VSC Deck"),
                "Large view has no title bar, settings button, or close button");
            Check(tileButtons.All(b => Math.Abs(b.ActualWidth * dpi.DpiScaleX - 64) < .1 &&
                Math.Abs(b.ActualHeight * dpi.DpiScaleY - 64) < .1), "All large buttons are exactly 64 x 64 physical pixels");
            Check(tileButtons.Select(FocusMarker).All(marker =>
                    Math.Abs(marker.Width * dpi.DpiScaleX - 24) < .1 &&
                    Math.Abs(marker.Height * dpi.DpiScaleY - 3) < .1 &&
                    marker.CornerRadius.TopLeft > 0),
                "Large focus markers are short three-pixel bars with rounded ends");
            var hotkeyFocusTarget = tileButtons[0];
            Check(PostMessage(new WindowInteropHelper(deck).Handle, 0x312, 100, 0),
                "Focus hotkey message is delivered to VSC Deck");
            PumpUntil(() => FocusMarker(hotkeyFocusTarget).Opacity == 1 &&
                NativeWindows.GetForegroundWindow() != 0);
            Check(FocusMarker(hotkeyFocusTarget).Opacity == 1 &&
                tileButtons.Skip(1).All(button => FocusMarker(button).Opacity == 0),
                "Hotkey navigation marks only the focused VS Code button");
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
            Check(tileButtons.All(button => button.Content is TextBlock label &&
                    label.Text.Split('\n') is [var position, var workspace, var state] &&
                    int.TryParse(position, out _) && workspace.Length > 0 &&
                    !workspace.Contains("[Dev Container:", StringComparison.OrdinalIgnoreCase) &&
                    state is "Idle" or "Run" or "Question" or "Done"),
                "Large buttons show exactly the number, workspace name, and state on three lines");
            Check(DeckWindow.WorkspaceDisplayName(new CodeWindow(0, 0, "",
                    "juno36_amr1 [Dev Container: Juno 36 @ devbox]",
                    @"docker:devbox:\home\azureuser\wrk\juno36_amr1")) == "juno36_amr1",
                "Large button workspace names omit remote-window decorations");
            var runningButton = tileButtons.First(b =>
                b.Content is TextBlock label && label.Text.Split('\n').Last() == "Run");
            Check(runningButton is not null, "Real live agent session displays Run");
            Check(DeckWindow.MinimumRunningPulseCycleMilliseconds == 1200 &&
                DeckWindow.MaximumRunningPulseCycleMilliseconds == 1700 &&
                runningButton!.Template?.FindName("RunningGlow", runningButton) is Border
                    { HasAnimatedProperties: true, Background: RadialGradientBrush },
                "Running button uses a prominent animated lamp glow on a random 1200–1700-millisecond cycle");
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
            Check(DeckWindow.IdleLampColor == Color.FromRgb(10, 60, 30) &&
                DeckWindow.RunningLampColor == Color.FromRgb(14, 49, 112) &&
                DeckWindow.QuestionLampColor == Color.FromRgb(66, 46, 4),
                "Idle, running, and question lamps use the darker resting palette");
            Check(Math.Abs(Luminance(DeckWindow.IdleLampColor) - Luminance(DeckWindow.RunningLampColor)) < 2,
                "Idle green matches the perceived brightness of the running lamp's dark-blue state");
            Check(Math.Abs(Luminance(DeckWindow.QuestionLampColor) - Luminance(DeckWindow.RunningLampColor)) < 2,
                "Question amber matches the perceived brightness of the other dark lamp states");
            Check(tileButtons.Where(button => button.Content is TextBlock label &&
                    label.Text.Contains("Question")).All(button =>
                    button.Content is TextBlock { Foreground: SolidColorBrush textBrush } &&
                    textBrush.Color == Colors.White),
                "Question buttons use white text against the dark amber lamp");
            var completionTime = DateTimeOffset.UtcNow;
            var backgroundCompletion = DeckWindow.UpdateCompletionPending(default,
                VSCDeck.Core.Activity.Running, VSCDeck.Core.Activity.Idle, false, completionTime);
            var foregroundCompletion = DeckWindow.UpdateCompletionPending(default,
                VSCDeck.Core.Activity.Running, VSCDeck.Core.Activity.Idle, true, completionTime);
            Check(backgroundCompletion.Pending && backgroundCompletion.ExpiresAt is null &&
                backgroundCompletion.InitialJitterMilliseconds is >= 0 and <= DeckWindow.MaximumBlipJitterMilliseconds &&
                foregroundCompletion.Pending &&
                foregroundCompletion.ExpiresAt == completionTime.AddMilliseconds(
                    DeckWindow.ForegroundCompletionBlipCount * DeckWindow.CompletionBlipCycleMilliseconds +
                    foregroundCompletion.InitialJitterMilliseconds) &&
                foregroundCompletion.InitialJitterMilliseconds is >= 0 and <= DeckWindow.MaximumBlipJitterMilliseconds &&
                !DeckWindow.UpdateCompletionPending(default, VSCDeck.Core.Activity.Unknown,
                    VSCDeck.Core.Activity.Idle, false, completionTime).Pending,
                "Completion latches with a one-time jitter and shows three green blips in the foreground");
            var question = DeckWindow.UpdateQuestionIndicator(default, VSCDeck.Core.Activity.Running,
                VSCDeck.Core.Activity.Question);
            Check(question.Pending &&
                question.InitialJitterMilliseconds is >= 0 and <= DeckWindow.MaximumBlipJitterMilliseconds &&
                DeckWindow.UpdateQuestionIndicator(question, VSCDeck.Core.Activity.Question,
                    VSCDeck.Core.Activity.Question) == question,
                "Question transitions receive one jittered blip offset");
            Check(DeckWindow.GetStateText(VSCDeck.Core.Activity.Idle, true, true) == "Done" &&
                DeckWindow.GetStateText(VSCDeck.Core.Activity.Idle, true, false) == "Idle" &&
                DeckWindow.GetStateText(VSCDeck.Core.Activity.Idle, false, true) == "Done" &&
                DeckWindow.GetStateText(VSCDeck.Core.Activity.Running, false, false) == "Run",
                "Completion labels say Done until focus acknowledgement and large running labels say Run");
            var idleButton = tileButtons.First(button => button != runningButton);
            DeckWindow.SetCompletionBlip(idleButton, new(true, 0));
            Check(DeckWindow.BlipOnMilliseconds == 50 &&
                DeckWindow.CompletionBlipCycleMilliseconds == 1000 &&
                idleButton.Template.FindName("CompletionGlow", idleButton) is Border
                    { HasAnimatedProperties: true, Background: RadialGradientBrush completionBrush } &&
                completionBrush.GradientStops.Any(stop => stop.Color.G > stop.Color.R && stop.Color.G > stop.Color.B),
                "Completed button uses a repeating green blip during the final 50 milliseconds of each 1000-millisecond cycle");
            DeckWindow.SetCompletionBlip(idleButton, default);
            Check(idleButton.Template.FindName("CompletionGlow", idleButton) is Border
                    { HasAnimatedProperties: false, Opacity: 0 },
                "Acknowledging completion removes the green blip");
            DeckWindow.SetQuestionBlip(idleButton, new(true, 0));
            Check(DeckWindow.QuestionBlipCycleMilliseconds == 1500 &&
                idleButton.Template.FindName("QuestionGlow", idleButton) is Border
                    { HasAnimatedProperties: true, Background: RadialGradientBrush questionBrush } &&
                questionBrush.GradientStops.Any(stop => stop.Color.R > stop.Color.B && stop.Color.G > stop.Color.B),
                "Question button uses its own repeating 50-millisecond yellow blip");
            DeckWindow.SetQuestionBlip(idleButton, default);
            Check(idleButton.Template.FindName("QuestionGlow", idleButton) is Border
                    { HasAnimatedProperties: false, Opacity: 0 },
                "Resolving a question removes its yellow blip");
            var independentlyBlippingButtons = tileButtons.Where(button => button != runningButton).Take(2).ToArray();
            foreach (var button in independentlyBlippingButtons)
            {
                DeckWindow.SetCompletionBlip(button, default);
                DeckWindow.SetQuestionBlip(button, default);
            }
            DeckWindow.SetCompletionBlip(independentlyBlippingButtons[0], new(true, 0));
            Check(independentlyBlippingButtons[0].Template.FindName("CompletionGlow",
                    independentlyBlippingButtons[0]) is Border { HasAnimatedProperties: true } &&
                independentlyBlippingButtons[1].Template.FindName("QuestionGlow",
                    independentlyBlippingButtons[1]) is Border { HasAnimatedProperties: false },
                "Starting one button's blip does not start another button's cycle");
            DeckWindow.SetQuestionBlip(independentlyBlippingButtons[1], new(true, 0));
            Check(independentlyBlippingButtons[0].Template.FindName("CompletionGlow",
                    independentlyBlippingButtons[0]) is Border { HasAnimatedProperties: true } &&
                independentlyBlippingButtons[1].Template.FindName("QuestionGlow",
                    independentlyBlippingButtons[1]) is Border { HasAnimatedProperties: true },
                "Each button starts its blip cycle independently when its event is detected");
            DeckWindow.SetCompletionBlip(independentlyBlippingButtons[0], default);
            DeckWindow.SetQuestionBlip(independentlyBlippingButtons[1], default);
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
                Check(tileButtons.Any(b => ((TextBlock)b.Content).Text.Split('\n').Last() == "Run"),
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

            var compactSettings = new Settings { ButtonSize = 32, Left = 200, Top = 180,
                RelativeOrder = settings.RelativeOrder,
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
            Check(compactButtons.Select(FocusMarker).All(marker =>
                    Math.Abs(marker.Width * dpi.DpiScaleX - 10) < .1 &&
                    Math.Abs(marker.Height * dpi.DpiScaleY - 1) < .1 &&
                    marker.CornerRadius == new CornerRadius(0)),
                "Compact focus markers are short one-pixel bars");
            Check(compactButtons.Select(ColorStripe).All(stripe =>
                    Math.Abs(stripe.Height * dpi.DpiScaleY - 1) < .1 &&
                    Math.Abs(stripe.Margin.Top * dpi.DpiScaleY - 1) < .1),
                "Compact workspace stripes are one physical pixel high with a one-pixel button gap");
            Check(compactButtons.All(b => VisualTreeHelper.GetParent(b) is StackPanel p &&
                p.Children.OfType<Border>().All(border =>
                    System.Windows.Automation.AutomationProperties.GetName(border) ==
                    "Workspace status bar color stripe")), "Compact buttons omit reorder grips");
            Check(Descendants<TextBlock>(compact).Where(t => t.Text == "VSC Deck  ::")
                    .All(t => !t.IsVisible) &&
                Descendants<Button>(compact).Where(b =>
                    System.Windows.Automation.AutomationProperties.GetName(b) is "Settings" or "Close VSC Deck")
                    .All(b => !b.IsVisible),
                "Compact view hides the title bar, settings button, and close button");
            var largeToggle = Descendants<Button>(compact).Single(b => Equals(b.Content, "▼"));
            var moveGrip = Descendants<Border>(compact).Single(b =>
                System.Windows.Automation.AutomationProperties.GetName(b) == "Move VSC Deck");
            Check(largeToggle.ToolTip is null && moveGrip.ToolTip is null,
                "Compact toggle and drag handle have no tooltips");
            Check(largeToggle.BorderThickness == new Thickness(0) &&
                largeToggle.Template.FindName("CaptionSurface", largeToggle) is Border
                    { Background: SolidColorBrush brush } && brush.Color.A == 0,
                "Compact expand toggle uses the flat transparent caption style");
            Check(Math.Abs(moveGrip.ActualWidth * dpi.DpiScaleX - 16) < .1 &&
                Math.Abs(moveGrip.ActualHeight - compact.ActualHeight) < .1,
                "Compact drag handle is 16 physical pixels wide and spans the full window height");
            if (settings.RelativeOrder.Count > 0)
                Check(System.Windows.Automation.AutomationProperties.GetName(compactButtons[0]) ==
                    System.Windows.Automation.AutomationProperties.GetName(tileButtons[0]), "Restart reconstructs reordered window positions");
            if (args.Length > 0) Screenshot(compact, Path.Combine(args[0], "panel-32.png"));
            largeToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => compactSettings.ButtonSize == 64 && TileButtons(compact).All(b =>
                Math.Abs(b.ActualWidth * dpi.DpiScaleX - 64) < .1));
            Check(!Descendants<TextBlock>(compact).Any(t => t.Text == "VSC Deck  ::"),
                "Switching to large view does not restore a title bar");
            Descendants<Button>(compact).Single(b => Equals(b.Content, "▲"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => compactSettings.ButtonSize == 32 && TileButtons(compact).All(b =>
                Math.Abs(b.ActualWidth * dpi.DpiScaleX - 32) < .1));
            Check(!Descendants<TextBlock>(compact).Any(t => t.Text == "VSC Deck  ::"),
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
            Check(viewOptions.Items.Cast<string>().SequenceEqual(["Large", "Compact", "Mini"]),
                "Settings exposes large, compact, and mini view options");
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

    private static int RunViewTests()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var originalSettings = File.Exists(AppStorage.SettingsPath) ? File.ReadAllText(AppStorage.SettingsPath) : null;
        var settings = new Settings { ButtonSize = 64 };
        var deck = new DeckWindow(settings);
        var dialog = new SettingsWindow(new Settings { ButtonSize = 20 });
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        try
        {
            // Keep the deck unshown: no monitor, global hotkeys, or VS Code activation.
            var tileType = typeof(DeckWindow).GetNestedType("Tile", System.Reflection.BindingFlags.NonPublic)!;
            var tiles = (System.Collections.IList)typeof(DeckWindow).GetField("tiles", flags)!.GetValue(deck)!;
            for (var index = 0; index < 16; index++)
                tiles.Add(Activator.CreateInstance(tileType,
                    new CodeWindow((nint)(index + 1), 0, $"Workspace {index + 1}", $"Project {index + 1}", null))!);
            var root = (FrameworkElement)deck.Content;
            void Layout()
            {
                root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                root.Arrange(new Rect(root.DesiredSize));
                root.UpdateLayout();
            }
            typeof(DeckWindow).GetMethod("BuildTiles", flags)!.Invoke(deck, null);
            Layout();
            var up = Descendants<Button>(root).Single(b => Equals(b.Content, "▲"));
            var down = Descendants<Button>(root).Single(b => Equals(b.Content, "▼"));
            var upPosition = up.TranslatePoint(new Point(), root);
            var downPosition = down.TranslatePoint(new Point(), root);
            foreach (var size in new[] { 64, 32, 20, 20, 32, 64, 64 })
            {
                if (settings.ButtonSize != size || size == 20 || size == 64)
                {
                    var smaller = size < settings.ButtonSize || size == 20;
                    (smaller ? up : down).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Layout();
                }
                Check(settings.ButtonSize == size, $"View navigation reaches and stays within {size}-pixel bounds");
                var dpi = VisualTreeHelper.GetDpi(deck);
                var buttons = Descendants<Button>(root).Where(b => b.Content is TextBlock).ToArray();
                Check(!Descendants<TextBlock>(root).Any(t => t.Text == "VSC Deck  ::") &&
                    !Descendants<Button>(root).Any(b =>
                        System.Windows.Automation.AutomationProperties.GetName(b) is "Settings" or "Close VSC Deck"),
                    $"The {size}-pixel view has no title bar or caption action buttons");
                Check(buttons.All(b => Math.Abs(b.TranslatePoint(new Point(), root).Y - 3) < .1),
                    $"The {size}-pixel tiles start at the top margin without a title-bar gap");
                var grip = Descendants<Border>(root).Single(b =>
                    System.Windows.Automation.AutomationProperties.GetName(b) == "Move VSC Deck");
                Check(grip.Visibility == Visibility.Visible &&
                    Math.Abs(grip.ActualWidth * dpi.DpiScaleX - 16) < .1 &&
                    Math.Abs(grip.ActualHeight - root.ActualHeight) < .1,
                    $"The {size}-pixel view retains a full-height drag handle");
                Check(grip.ContextMenu is { } menu &&
                    menu.Items.OfType<MenuItem>().Select(item => item.Header).SequenceEqual(["Settings", "Exit"]),
                    $"The {size}-pixel view retains Settings and Exit through the drag-handle menu");
                Check(buttons.Length == 16 && buttons.All(b =>
                        Math.Abs(b.ActualWidth * dpi.DpiScaleX - size) < .1 &&
                        Math.Abs(b.ActualHeight * dpi.DpiScaleY - size) < .1),
                    $"All sixteen tiles measure exactly {size} x {size} physical pixels");
                Check(up.Visibility == Visibility.Visible &&
                    down.Visibility == Visibility.Visible &&
                    up.IsEnabled == (size > 20) && down.IsEnabled == (size < 64),
                    $"Arrow visibility and enabled states match the {size}-pixel view");
                Check(Grid.GetColumn(up) == 0 && Grid.GetColumn(down) == 1 &&
                    up.TranslatePoint(new Point(), root) == upPosition &&
                    down.TranslatePoint(new Point(), root) == downPosition &&
                    downPosition.X > upPosition.X && downPosition.Y == upPosition.Y &&
                    downPosition.X + down.ActualWidth <= buttons[0].TranslatePoint(new Point(), root).X,
                    $"The arrows stay side by side at fixed positions to the left of the {size}-pixel tiles");
                Check(new[] { up, down }.All(button =>
                    Math.Abs(button.ActualWidth * dpi.DpiScaleX - 16) < .1 &&
                    Math.Abs(button.ActualHeight * dpi.DpiScaleY - 20) < .1),
                    $"The arrows retain fixed 16 x 20 physical-pixel bounds at {size} pixels");
                var controlsGrip = Descendants<Border>(root).Single(b =>
                    System.Windows.Automation.AutomationProperties.GetName(b) == "Move VSC Deck below size controls");
                Check(controlsGrip.Visibility == (size == 20 ? Visibility.Collapsed : Visibility.Visible) &&
                    controlsGrip.Cursor == System.Windows.Input.Cursors.SizeAll &&
                    ReferenceEquals(controlsGrip.ContextMenu, grip.ContextMenu),
                    $"The additional move grip is available only in Compact and Large, including its context menu");
                if (size > 20)
                {
                    Check(controlsGrip.ActualHeight > 0 &&
                        controlsGrip.TranslatePoint(new Point(), root).Y >= downPosition.Y + down.ActualHeight &&
                        Math.Abs(controlsGrip.ActualWidth - up.ActualWidth - down.ActualWidth) < .1 &&
                        Math.Abs(controlsGrip.TranslatePoint(new Point(0, controlsGrip.ActualHeight), root).Y -
                            (((Grid)root).RowDefinitions[0].ActualHeight - 3)) < .1,
                        $"The {size}-pixel move grip fills the remaining space beneath both arrows");
                    var column = (StackPanel)VisualTreeHelper.GetParent(buttons[0]);
                    Check(((Grid)root).RowDefinitions[0].ActualHeight <= column.ActualHeight + 6 + .1,
                        $"The {size}-pixel move grip does not increase the tile bar height");
                }
                if (size == 20)
                {
                    var controls = (Grid)VisualTreeHelper.GetParent(down);
                    var leftControls = (Grid)VisualTreeHelper.GetParent(controls);
                    var column = (StackPanel)VisualTreeHelper.GetParent(buttons[0]);
                    Check(Math.Abs(downPosition.Y + down.ActualHeight / 2 -
                        buttons[0].TranslatePoint(new Point(0, buttons[0].ActualHeight / 2), root).Y) < .1,
                        "Mini arrows are vertically centered on the VS Code tiles");
                    Check(controls.ActualHeight <= column.ActualHeight + .1,
                        "Mini controls add no height beyond the tiles and workspace color stripe");
                    Check(((Grid)root).RowDefinitions[0].ActualHeight <=
                        column.ActualHeight + leftControls.Margin.Top + leftControls.Margin.Bottom + .1,
                        "Mini tile row has no extra vertical space from the size controls");
                }
                Check(buttons.Select((b, index) => ((TextBlock)b.Content).Text ==
                    (size == 20 ? (index + 1).ToString() : size == 32
                        ? $"{index + 1}\n?" : $"{index + 1}\nProject {index + 1}\nUnknown")).All(match => match),
                    $"Tile labels match the exact {size}-pixel layout, including two-digit positions");
                Check(buttons.All(b => b.Background is LinearGradientBrush &&
                    b.Template.FindName("GlassSheen", b) is Border),
                    $"Glass colors and sheen remain present at {size} pixels");
                Check(buttons.Select(FocusMarker).All(marker =>
                    Math.Abs(marker.Width * dpi.DpiScaleX - (size == 20 ? 6 : size == 32 ? 10 : 24)) < .1 &&
                    Math.Abs(marker.Height * dpi.DpiScaleY - (size < 64 ? 1 : 3)) < .1),
                    $"Focus markers fit within {size}-pixel tiles");
                Check(buttons.Select(ColorStripe).All(stripe =>
                    Math.Abs(stripe.Height * dpi.DpiScaleY - (size < 64 ? 1 : 2)) < .1),
                    $"Workspace color stripes keep their physical-pixel height at {size} pixels");
                Check(buttons.All(b => b.ToolTip is TextBlock { Text.Length: > 0 } &&
                    System.Windows.Automation.AutomationProperties.GetHelpText(b)
                        .StartsWith(size == 32 ? "?." : "Unknown.", StringComparison.Ordinal)),
                    $"Hover and accessible status details remain available at {size} pixels");
                foreach (var button in buttons)
                {
                    DeckWindow.SetRunningPulse(button, true);
                    DeckWindow.SetCompletionBlip(button, new(true, 0));
                    DeckWindow.SetQuestionBlip(button, new(true, 0));
                    Check(new[] { "RunningGlow", "CompletionGlow", "QuestionGlow" }.All(name =>
                        HasAnimation(button, name)), $"All status animations work at {size} pixels");
                    DeckWindow.SetRunningPulse(button, false);
                    DeckWindow.SetCompletionBlip(button, default);
                    DeckWindow.SetQuestionBlip(button, default);
                }
                if (size == 20)
                {
                    AppStorage.Save(settings);
                    Check(AppStorage.Load().ButtonSize == 20, "Mini persists and reloads without migration");
                }
            }
            AppStorage.Save(new Settings { ButtonSize = 16 });
            Check(AppStorage.Load().ButtonSize == 20, "Legacy 16-pixel Mini settings migrate to 20 pixels");
            using (var saved = JsonDocument.Parse(File.ReadAllText(AppStorage.SettingsPath)))
                Check(saved.RootElement.GetProperty("ButtonSize").GetInt32() == 20,
                    "Migrated 20-pixel Mini size is persisted");
            AppStorage.Save(new Settings { ButtonSize = 30 });
            Check(AppStorage.Load().ButtonSize == 32,
                "Legacy 30-pixel Compact settings still migrate to 32 pixels");
            var view = Descendants<ComboBox>((FrameworkElement)dialog.Content).Single();
            Check(Equals(view.SelectedItem, "Mini") &&
                view.Items.Cast<string>().SequenceEqual(["Large", "Compact", "Mini"]),
                "Settings exposes all three views and restores Mini selection");
            Console.WriteLine($"All {checks} view checks passed without interacting with VS Code.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            deck.Close();
            dialog.Close();
            if (originalSettings is not null) File.WriteAllText(AppStorage.SettingsPath, originalSettings);
            else if (File.Exists(AppStorage.SettingsPath)) File.Delete(AppStorage.SettingsPath);
            app.Shutdown();
        }
        static bool HasAnimation(Button button, string name) =>
            button.Template.FindName(name, button) is Border { HasAnimatedProperties: true };
    }

    private static int RunPulseTests()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var button = new Button
        {
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 TargetType="Button">
                    <Border x:Name="RunningGlow" Opacity="0"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
                </ControlTemplate>
                """)
        };
        var target = new Window
        {
            Title = "VSC Deck isolated pulse test", Content = button, Width = 80, Height = 80,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        try
        {
            var pulses = Enumerable.Range(0, 2000).Select(_ => DeckWindow.CreateRunningPulse()).ToArray();
            var cycles = pulses.Select(pulse => pulse.Duration.TimeSpan.TotalMilliseconds * 2).ToArray();
            Check(cycles.All(cycle => cycle is >= 1200 and <= 1700 && cycle == Math.Truncate(cycle)),
                "Every sampled full cycle is an integer between 1200 and 1700 milliseconds inclusive");
            Check(cycles.Distinct().Count() > 1 && cycles.Any(cycle => cycle % 2 == 1),
                "New pulses vary in duration and preserve half-millisecond portions for odd cycle lengths");
            Check(pulses.All(pulse => pulse.AutoReverse && pulse.RepeatBehavior == RepeatBehavior.Forever &&
                    pulse.From == .08 && pulse.To == .92 &&
                    pulse.EasingFunction is SineEase { EasingMode: EasingMode.EaseInOut }),
                "Pulses have equal rise and fall portions and retain the existing glow and easing");
            target.Show();
            DeckWindow.SetRunningPulse(button, true);
            var glow = (Border)button.Template.FindName("RunningGlow", button);
            PumpUntil(() => glow.Opacity > .3);
            var opacity = glow.Opacity;
            DeckWindow.SetRunningPulse(button, true);
            Check(glow.HasAnimatedProperties && glow.Opacity == opacity,
                "Repeated Run updates retain the existing pulse rather than restarting it");
            DeckWindow.SetRunningPulse(button, false);
            Check(!glow.HasAnimatedProperties && glow.Opacity == 0,
                "Leaving Run removes the pulse and clears the glow");
            DeckWindow.SetRunningPulse(button, true);
            Check(glow.HasAnimatedProperties && glow.Opacity < .3,
                "Re-entering Run starts a fresh pulse");
            DeckWindow.SetRunningPulse(button, false);
            Console.WriteLine($"All {checks} pulse checks passed without interacting with VS Code.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            DeckWindow.SetRunningPulse(button, false);
            target.Close();
            app.Shutdown();
        }
    }

    private static int RunSnappingTests()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var target = new Window
        {
            Title = "VSC Deck isolated snapping test", Width = 300, Height = 150,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        try
        {
            var monitor = new NativeWindows.PixelRect(-1920, -200, 0, 880);
            foreach (var delta in new[] { -24, 0, 24 })
            {
                Check(NativeWindows.CalculateSnapOffset(
                    new(-1920 + delta, -200 + delta, -1520 + delta, 100 + delta), monitor, 24) ==
                    new NativeWindows.PixelOffset(-delta, -delta),
                    $"Top-left snapping is flush at tolerance {delta} on a negative-coordinate monitor");
                Check(NativeWindows.CalculateSnapOffset(
                    new(-400 + delta, 580 + delta, delta, 880 + delta), monitor, 24) ==
                    new NativeWindows.PixelOffset(-delta, -delta),
                    $"Bottom-right snapping is flush at tolerance {delta}");
            }
            Check(NativeWindows.CalculateSnapOffset(
                new(-1895, -175, -1495, 125), monitor, 24) == default &&
                NativeWindows.CalculateSnapOffset(
                    new(-425, 555, -25, 855), monitor, 24) == default,
                "Windows 25 pixels from the screen edge do not snap");
            var workArea = new NativeWindows.PixelRect(-1920, -200, 0, 840);
            foreach (var delta in new[] { -24, 0, 24 })
            {
                var bottom = workArea.Bottom + delta;
                var expected = delta == 24 ? 16 : -delta;
                Check(NativeWindows.CalculateSnapOffset(
                    new(-1000, bottom - 100, -600, bottom), monitor, 24, workArea) ==
                    new NativeWindows.PixelOffset(0, expected),
                    $"The nearest bottom screen or taskbar edge wins at taskbar offset {delta}");
            }
            Check(NativeWindows.CalculateSnapOffset(new(-1000, 750, -600, 850), monitor, 24, workArea) ==
                new NativeWindows.PixelOffset(0, -10),
                "The taskbar edge wins when the screen edge is outside the threshold");
            Check(NativeWindows.CalculateSnapOffset(new(-1000, 770, -600, 870), monitor, 24, workArea) ==
                new NativeWindows.PixelOffset(0, 10),
                "The screen edge wins when the taskbar edge is outside the threshold");
            Check(NativeWindows.CalculateSnapOffset(new(-1000, 760, -600, 860), monitor, 24, workArea) ==
                new NativeWindows.PixelOffset(0, 20),
                "Equal-distance bottom targets retain the screen-edge preference");
            Check(NativeWindows.CalculateSnapOffset(new(-1000, 715, -600, 815), monitor, 24, workArea) == default,
                "A window outside both bottom snap thresholds remains unmoved");
            foreach (var otherWorkArea in new[]
                {
                    monitor,
                    new NativeWindows.PixelRect(-1880, -200, 0, 880),
                    new NativeWindows.PixelRect(-1920, -160, 0, 880),
                    new NativeWindows.PixelRect(-1920, -200, -40, 880)
                })
                Check(NativeWindows.CalculateSnapOffset(
                    new(-1870, -150, -1470, -50), monitor, 24, otherWorkArea) == default,
                    "No taskbar or a top/side taskbar adds no new snap targets");
            target.Show();
            CheckScreenEdgeSnapping(new WindowInteropHelper(target).Handle);
            Console.WriteLine($"All {checks} snapping checks passed without interacting with VS Code.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            target.Close();
            app.Shutdown();
        }
    }

    private static void CheckScreenEdgeSnapping(nint handle)
    {
        var target = NativeWindows.GetWindowBounds(handle);
        var screen = NativeWindows.GetMonitorBounds(handle);
        var width = target.Right - target.Left;
        var height = target.Bottom - target.Top;
        Check(SetWindowPos(handle, 0, screen.Left + 24, screen.Top + 24, width, height, 0x0004 | 0x0010),
            "Test window positioned 24 px from the top-left screen edges");
        Check(DeckWindow.ApplyScreenEdgeSnap(handle, false) == default &&
            NativeWindows.GetWindowBounds(handle) is var unsnapped &&
            unsnapped.Left == screen.Left + 24 && unsnapped.Top == screen.Top + 24,
            "Snapping disabled leaves a real window unmoved");
        Check(DeckWindow.ApplyScreenEdgeSnap(handle, true) == new NativeWindows.PixelOffset(-24, -24) &&
            NativeWindows.GetWindowBounds(handle) is var topLeft &&
            topLeft.Left == screen.Left && topLeft.Top == screen.Top,
            "A real window within 24 physical pixels snaps flush to the top and left screen edges");
        Check(SetWindowPos(handle, 0, screen.Right - width - 24, screen.Bottom - height,
            width, height, 0x0004 | 0x0010), "Test window positioned at the bottom and 24 px from the right screen edge");
        Check(DeckWindow.ApplyScreenEdgeSnap(handle, true) == new NativeWindows.PixelOffset(24, 0) &&
            NativeWindows.GetWindowBounds(handle) is var bottomRight &&
            bottomRight.Right == screen.Right && bottomRight.Bottom == screen.Bottom,
            "A real window retains the bottom screen edge while snapping flush to the right");
        var workArea = NativeWindows.GetMonitorWorkArea(handle);
        if (workArea.Bottom < screen.Bottom)
        {
            Check(SetWindowPos(handle, 0, screen.Left + 100, workArea.Bottom - height - 10,
                width, height, 0x0004 | 0x0010), "Test window positioned 10 px above the bottom taskbar");
            Check(DeckWindow.ApplyScreenEdgeSnap(handle, false) == default &&
                NativeWindows.GetWindowBounds(handle).Bottom == workArea.Bottom - 10,
                "Disabled snapping does not snap to the taskbar");
            Check(DeckWindow.ApplyScreenEdgeSnap(handle, true) == new NativeWindows.PixelOffset(0, 10) &&
                NativeWindows.GetWindowBounds(handle).Bottom == workArea.Bottom,
                "A real window snaps flush above the bottom taskbar");
        }
        Check(SetWindowPos(handle, 0, screen.Left + 25, screen.Top + 25, width, height, 0x0004 | 0x0010),
            "Test window positioned just outside the 24 px threshold");
        Check(DeckWindow.ApplyScreenEdgeSnap(handle, true) == default &&
            NativeWindows.GetWindowBounds(handle) is var outside &&
            outside.Left == screen.Left + 25 && outside.Top == screen.Top + 25,
            "A real window 25 physical pixels from the screen edges remains unmoved");
    }

    private static int RunDiscoveryTests()
    {
        try
        {
            var window = new CodeWindow(0, 0,
                "test-vertex.sh - spike24_gemini_metrics [Dev Container: Gemini Metrics @ desktop-linux] - Visual Studio Code",
                "spike24_gemini_metrics [Dev Container: Gemini Metrics @ desktop-linux]", null);
            var json = JsonSerializer.Serialize(new[]
            {
                new
                {
                    Config = new
                    {
                        User = "root",
                        Labels = new Dictionary<string, string>
                        {
                            ["devcontainer.local_folder"] = @"c:\Bren\juno\spike24_gemini_metrics",
                            ["devcontainer.metadata"] = """[{"remoteUser":"vscode"}]"""
                        }
                    }
                }
            });
            var discovered = RemoteSourceDiscovery.ParseContainers(json, "desktop-linux", null, [window]);
            Check(discovered is [{ Project: "spike24_gemini_metrics",
                    SessionRoot: "/home/vscode/.copilot/session-state" }] && discovered[0].Matches(window),
                "Spike24 discovery matches its window and prefers remoteUser over the container root user");
            Check(RemoteSourceDiscovery.ParseContainers(json, "desktop-linux", null, []).Count == 0,
                "Discovery does not add containers without an open matching window");
            var sshWindow = new CodeWindow(3, 0, "juno59_nlwf [SSH: devbox] - Visual Studio Code",
                "juno59_nlwf [SSH: devbox]", null);
            var sshJson = JsonSerializer.Serialize(new
            {
                root = "/home/azureuser/.copilot/session-state",
                workspaces = new[] { "/home/azureuser/wrk/juno59_nlwf" },
                metadata = new[]
                {
                    "cwd: /home/azureuser/wrk/juno59_nlwf\nclient_name: vscode-agent-host\n",
                    "cwd: /other/juno59_nlwf\nclient_name: copilot-cli\n"
                }
            });
            var sshResult = RemoteSourceDiscovery.ParseSshWorkspaces(sshJson, "devbox", [sshWindow]);
            Check(sshResult.Sources is [{ DirectSsh: true, Project: "juno59_nlwf",
                    Workspace: "/home/azureuser/wrk/juno59_nlwf", SessionRoot: "/home/azureuser/.copilot/session-state" }] &&
                sshResult.Warnings.Count == 0,
                "Direct SSH discovery matches IDE and agent-host metadata using the host user's session root");
            var sshSource = sshResult.Sources[0];
            Check(sshSource.Matches(sshWindow) &&
                !sshSource.Matches(sshWindow with { Project = "juno59_nlwf [SSH: other-host]" }) &&
                !sshSource.Matches(sshWindow with { Project = "juno59_nlwf [Dev Container: Azure Terraform @ devbox]" }),
                "Direct SSH sources match only the exact project and SSH host, not container windows");
            var samePathContainer = new RemoteSource
            {
                Project = sshSource.Project, Workspace = sshSource.Workspace, SshHost = sshSource.SshHost
            };
            Check(sshSource.Identity.StartsWith("ssh:devbox:") &&
                RemoteSourceDiscovery.MergeSources([sshSource], [samePathContainer]).Count == 2,
                "Direct SSH and container sources for the same host path have distinct identities");
            Check(sshSource.MatchesWorkspace("/home/azureuser/wrk/juno59_nlwf") &&
                !sshSource.MatchesWorkspace("/other/juno59_nlwf"),
                "Direct SSH sessions use exact workspace paths rather than matching another directory with the same name");
            Check(RemoteSourceDiscovery.WindowHost(sshWindow) == "devbox",
                "Runtime discovery recognizes plain Remote-SSH window titles");
            var ambiguousJson = JsonSerializer.Serialize(new
            {
                root = "/home/azureuser/.copilot/session-state",
                workspaces = new[] { "/one/juno59_nlwf", "/two/juno59_nlwf" },
                metadata = Array.Empty<string>()
            });
            var ambiguous = RemoteSourceDiscovery.ParseSshWorkspaces(ambiguousJson, "devbox", [sshWindow]);
            Check(ambiguous.Sources.Count == 0 && ambiguous.Warnings.Count == 1,
                "Ambiguous Remote-SSH directory names are reported instead of guessed");
            Check(RemoteSourceDiscovery.ParseSshWorkspaces(sshJson, "other-host", [sshWindow]).Sources.Count == 0 &&
                RemoteSourceDiscovery.ParseSshWorkspaces(sshJson, "devbox", [window]).Sources.Count == 0,
                "SSH workspace discovery excludes other hosts and dev-container windows");
            var sshRuntime = new RuntimeSourceDiscovery(_ => Task.FromResult(sshResult));
            var sshNow = DateTimeOffset.UtcNow;
            sshRuntime.Poll([sshWindow], [], sshNow);
            Check(sshRuntime.Poll([sshWindow], [], sshNow.AddSeconds(1)).Sources.Single().DirectSsh,
                "A newly opened plain Remote-SSH window is automatically discovered at runtime");
            sshSource.DockerContext = "";
            sshSource.Validate();
            Check(JsonSerializer.Deserialize<RemoteSource>(JsonSerializer.Serialize(sshSource)) is { DirectSsh: true },
                "Direct SSH sources persist their transport without requiring a Docker context");
            Check(JsonSerializer.Deserialize<RemoteSource>("""{"Project":"old","Workspace":"/work/old","SshHost":"devbox"}""")
                is { DirectSsh: false, Identity: "docker:devbox:\\work\\old" },
                "Existing SSH-hosted container settings keep their original transport and identity");
            var configured = new RemoteSource
            {
                Project = "existing", Workspace = @"c:\work\existing",
                SessionRoot = "/custom/.copilot/session-state", ProjectIdentity = @"c:\canonical\existing"
            };
            var merged = RemoteSourceDiscovery.MergeSources([configured], discovered);
            Check(merged.Count == 2 && ReferenceEquals(merged[0], configured) &&
                merged[1].Matches(window),
                "Discovery adds a newly opened container even when settings already contain other sources");
            var duplicate = new RemoteSource
            {
                Project = configured.Project, Workspace = configured.Workspace.ToUpperInvariant()
            };
            merged = RemoteSourceDiscovery.MergeSources(merged, [duplicate, .. discovered]);
            Check(merged.Count == 2 && ReferenceEquals(merged[0], configured) &&
                merged[0].SessionRoot == "/custom/.copilot/session-state" &&
                merged[0].ProjectIdentity == @"c:\canonical\existing",
                "Repeated discovery is case-insensitively deduplicated and retains custom source settings");
            Check(RemoteSourceDiscovery.MergeSources(merged, []).SequenceEqual(merged),
                "Unavailable discovery retains all previously configured sources");
            Check(RemoteSourceDiscovery.MergeSources([], discovered).SequenceEqual(discovered),
                "First-launch discovery still adds all matching sources");
            var now = DateTimeOffset.UtcNow;
            var calls = 0;
            var completion = new TaskCompletionSource<RemoteDiscoveryResult>();
            var runtime = new RuntimeSourceDiscovery(targets =>
            {
                calls++;
                Check(targets.All(target => target.Project.Contains("[Dev Container:")),
                    "Runtime discovery only probes unconfigured dev-container windows");
                return completion.Task;
            });
            var local = new CodeWindow(1, 0, "local - Visual Studio Code", "local", null);
            runtime.Poll([local], [configured], now);
            Check(calls == 0, "Ordinary local windows do not trigger Docker or SSH discovery");
            var pending = runtime.Poll([local, window], [configured], now);
            Check(calls == 1 && pending.Sources.Count == 0,
                "Opening a new container window starts background discovery without blocking the scan");
            runtime.Poll([local, window], [configured], now.AddSeconds(1));
            Check(calls == 1, "Repeated scans do not start overlapping discovery");
            var secondWindow = window with { Handle = 2, Project = "new-project [Dev Container: New @ desktop-linux]" };
            completion.SetResult(new([], ["Temporarily unavailable"]));
            completion = new TaskCompletionSource<RemoteDiscoveryResult>();
            runtime.Poll([local, window, secondWindow], [configured], now.AddSeconds(2));
            Check(calls == 2, "A newly opened window triggers discovery without waiting for an older failure's retry");
            var secondSource = new RemoteSource { Project = "new-project", Workspace = @"c:\work\new-project" };
            completion.SetResult(new([.. discovered, secondSource], []));
            var result = runtime.Poll([local, window, secondWindow], [configured], now.AddSeconds(3));
            Check(result.Sources.Count == 2 && result.Warnings.Count == 0,
                "Completed runtime discovery returns new sources and clears resolved warnings");
            var allSources = RemoteSourceDiscovery.MergeSources([configured], result.Sources);
            runtime.Poll([local, window, secondWindow], allSources, now.AddSeconds(60));
            Check(calls == 2, "Configured windows do not rediscover or recreate their existing monitors");

            var retries = 0;
            var retrying = new RuntimeSourceDiscovery(_ =>
            {
                retries++;
                return Task.FromResult(new RemoteDiscoveryResult([], ["Unavailable"]));
            });
            retrying.Poll([window], [], now);
            var failure = retrying.Poll([window], [], now.AddSeconds(1));
            Check(failure.Warnings.SequenceEqual(["Unavailable"]), "Discovery failures remain visible while a source is missing");
            retrying.Poll([window], [], now.AddSeconds(30));
            Check(retries == 1, "An unavailable source does not cause discovery on every scan");
            retrying.Poll([window], [], now.AddSeconds(31));
            Check(retries == 2, "An unavailable source is retried 30 seconds after discovery completes");
            var closed = retrying.Poll([], [], now.AddSeconds(32));
            Check(closed.Warnings.Count == 0 && closed.Sources.Count == 0,
                "Closing an unconfigured window clears its discovery warning");
            retrying.Poll([], [], now.AddSeconds(120));
            Check(retries == 2, "Closed windows do not trigger further discovery");

            var late = new TaskCompletionSource<RemoteDiscoveryResult>();
            var closingRuntime = new RuntimeSourceDiscovery(_ => late.Task);
            closingRuntime.Poll([window], [], now);
            late.SetResult(new(discovered, []));
            Check(closingRuntime.Poll([], [], now.AddSeconds(1)).Sources.Count == 0,
                "A discovery result for a window closed during the probe is discarded");

            var order = Identity.RelationsFor(["existing-a", "existing-b"]);
            var ordered = Identity.Sort(["existing-a", "existing-b", window.Identity], order, identity => identity);
            Check(ordered.SequenceEqual(["existing-a", "existing-b", window.Identity]),
                "A never-seen window is appended after existing windows");
            ordered[^1] = discovered[0].OrderIdentity;
            Check(Identity.Sort(ordered, order, identity => identity).SequenceEqual(ordered),
                "Resolving a new window's source retains its appended position");
            Console.WriteLine($"All {checks} discovery checks passed without interacting with VS Code.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int RunLiveDiscoveryTest(string[] args)
    {
        try
        {
            var settings = AppStorage.Load();
            var option = Array.IndexOf(args, "--runtime-discovery-live");
            if (option + 1 >= args.Length)
                throw new ArgumentException("--runtime-discovery-live requires the project name of an open dev-container window.");
            var project = args[option + 1];
            Check(settings.RemoteSources.Any(source => source.Project == project),
                "The live test project has a saved source that can be omitted from the test monitor");
            var configured = settings.RemoteSources.Where(source => source.Project != project).ToList();
            using var monitor = new CopilotMonitor(configured);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            MonitorSnapshot snapshot;
            do
            {
                snapshot = monitor.Scan();
                var window = snapshot.Windows.SingleOrDefault(window => window.Project.StartsWith(project + " ["));
                if (window?.Workspace is { } workspace &&
                    snapshot.Sessions.Any(session => session.Workspace == workspace))
                {
                    Check(snapshot.RemoteSources.Count == configured.Count + 1,
                        "The live monitor automatically adds the omitted project to its initially configured sources");
                    Check(snapshot.RemoteSources.Take(configured.Count).SequenceEqual(configured),
                        "Runtime discovery retains all existing source objects and their order");
                    Check(snapshot.Warnings.Count == 0, "Live runtime discovery completes without status warnings");
                    Console.WriteLine($"Live runtime discovery detected {project} telemetry without restarting or interacting with VS Code.");
                    return 0;
                }
                Thread.Sleep(750);
            } while (DateTimeOffset.UtcNow < deadline);
            throw new TimeoutException($"Runtime discovery did not detect the live {project} session.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int RunContainerReaderTests(string[] args)
    {
        try
        {
            var index = Array.IndexOf(args, "--container-reader-only");
            if (index + 2 >= args.Length)
                throw new ArgumentException("--container-reader-only requires an SSH host and a saved dev-container project.");
            var host = args[index + 1];
            var project = args[index + 2];
            using var channel = new SshCommandChannel(host);
            var fixtureInput = JsonSerializer.Serialize(new
            {
                reader = RemoteMonitor.HelperScript, scanScript = RemoteMonitor.ContainerOwnersScript
            });
            var fixtureOutput = channel.Run(["python3", "-c", ContainerReaderFixture], fixtureInput).GetAwaiter().GetResult();
            Check(fixtureOutput.Trim() == "All 12 host container-reader fixture checks passed.",
                "Host reader preserves byte offsets, partial lines, truncation, filtering, ownership timestamps, and explicit failures");
            var source = AppStorage.Load().RemoteSources.Single(source =>
                source.Project == project && source.SshHost == host && !source.DirectSsh);
            var ids = channel.Run(["docker", "ps", "--filter", "label=devcontainer.local_folder=" + source.Workspace,
                "--format", "{{.ID}}"]).GetAwaiter().GetResult()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            Check(ids.Length == 1, "The live no-Python container is uniquely identified by its saved workspace label");
            var missingPython = false;
            try { channel.Run(["docker", "exec", ids[0], "python3", "--version"]).GetAwaiter().GetResult(); }
            catch (IOException ex) { missingPython = ex.Message.Contains("exited 127"); }
            Check(missingPython, "The live test container has no python3 executable");
            var window = new CodeWindow(0, 0, "", $"{project} [Dev Container: Test @ {host}]", null);
            using var monitor = new RemoteMonitor([source]);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            do
            {
                var snapshot = monitor.Scan([window]);
                if (snapshot.Available.Contains(source.Identity) && snapshot.Sessions.Count > 0)
                {
                    Check(snapshot.Warnings.Count == 0 && snapshot.Errors.Count == 0,
                        "A container without Python reports live sessions without monitoring errors");
                    Check(snapshot.Sessions.All(session => session.Workspace == source.Identity),
                        "Host-read telemetry is associated with the container workspace, not the host workspace");
                    Console.WriteLine($"Live host-side reader detected {snapshot.Sessions.Count} session(s) in {project} without container Python.");
                    return 0;
                }
                if (snapshot.Errors.Count > 0)
                    throw new IOException(string.Join("\n", snapshot.Errors.Values));
                Thread.Sleep(250);
            } while (DateTimeOffset.UtcNow < deadline);
            throw new TimeoutException($"Host-side container reader did not detect live telemetry in {project}.");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private const string ContainerReaderFixture = """
import contextlib, io, json, os, subprocess, sys, tempfile, types
fixture = json.load(sys.stdin)
root = "/home/vscode/.copilot/session-state"
ident = "fixture-session"
commands = []
mode = "normal"
def event(kind, content):
    return (json.dumps({"type": kind, "timestamp": "2026-10-19T00:00:00Z",
                        "data": {"content": content, "unused": "not transferred"}}, ensure_ascii=False) + "\n").encode()
complete = event("user.message", "\u03c0") + event("ignored.event", "skip")
partial = event("assistant.message", "partial")
data = complete + partial[:-1]
def run(argv, **kwargs):
    assert argv[:3] == ["docker", "exec", "fixture-container"], argv
    command = argv[3:]
    commands.append(command)
    if mode == "failure":
        return types.SimpleNamespace(returncode=7, stdout=b"read failed", stderr=b"")
    if command[0] == "sh":
        assert command == ["sh", "-c", fixture["scanScript"], "vscdeck", root]
        output = b"unavailable\0" if mode == "missing" else b"available\0" + b"1000\0" + b"100\0"
        if mode == "normal":
            output += ident.encode() + b"\0" + b"150 200\0"
    elif command[0] == "stat":
        output = str(len(data)).encode()
    elif command[0] == "tail":
        assert command[1] == "-c"
        output = data[int(command[2][1:]) - 1:]
    elif command[0] == "cat":
        output = b"cwd: /work/project\nclient_name: vscode-agent-host\n"
    else:
        raise AssertionError(command)
    return types.SimpleNamespace(returncode=0, stdout=output, stderr=b"")
subprocess.run = run
def read(offsets, session_root=root, container="fixture-container"):
    request = {"root": session_root, "offsets": offsets, "container": container,
               "scanScript": fixture["scanScript"]}
    output = io.StringIO()
    previous_input = sys.stdin
    try:
        sys.stdin = io.StringIO(json.dumps(request))
        with contextlib.redirect_stdout(output):
            exec(compile(fixture["reader"], "container-reader", "exec"), {})
    finally:
        sys.stdin = previous_input
    return json.loads(output.getvalue())
first = read({})["sessions"][0]
assert first["offset"] == len(complete) and [e["type"] for e in first["events"]] == ["user.message"]
assert first["events"][0]["data"] == {"content": "\u03c0"} and not first["reset"]
assert first["started"] == 1001.5
unchanged = read({ident: first["offset"]})["sessions"][0]
assert unchanged["events"] == [] and unchanged["offset"] == len(complete)
assert commands[-2] == ["tail", "-c", "+" + str(len(complete) + 1), "--", root + "/" + ident + "/events.jsonl"]
data += b"\n"
appended = read({ident: unchanged["offset"]})["sessions"][0]
assert appended["offset"] == len(data) and [e["type"] for e in appended["events"]] == ["assistant.message"]
data = event("session.idle", "idle")
truncated = read({ident: appended["offset"]})["sessions"][0]
assert truncated["reset"] and truncated["offset"] == len(data)
mode = "empty"
assert read({}) == {"available": True, "sessions": []}
mode = "missing"
assert read({}) == {"available": False, "sessions": []}
mode = "failure"
error = io.StringIO()
with contextlib.redirect_stderr(error):
    try:
        read({})
        raise AssertionError("A failed Docker read must not report healthy status")
    except SystemExit as ex:
        assert ex.code == 7 and "read failed" in error.getvalue()
with tempfile.TemporaryDirectory(prefix="vscdeck-reader-") as temporary:
    directory = os.path.join(temporary, ident)
    os.mkdir(directory)
    with open(os.path.join(directory, "workspace.yaml"), "w") as stream:
        stream.write("cwd: /work/project\nclient_name: vscode-agent-host\n")
    with open(os.path.join(directory, "events.jsonl"), "wb") as stream:
        stream.write(data)
    with open(os.path.join(directory, "inuse." + str(os.getpid()) + ".lock"), "w"):
        pass
    local = read({}, temporary, None)
    assert local["available"] and len(local["sessions"]) == 1 and local["sessions"][0]["offset"] == len(data)
    assert read({ident: len(data)}, temporary, None)["sessions"][0]["events"] == []
print("All 12 host container-reader fixture checks passed.")
""";

    private static int RunAudioTests()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "vscdeck-audio-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var previous in Enum.GetValues<Activity>())
            foreach (var current in Enum.GetValues<Activity>())
            {
                StatusSound? expected = previous == current || previous == Activity.Unknown ? null :
                    current == Activity.Running ? StatusSound.Run :
                    current == Activity.Question ? StatusSound.Question :
                    previous == Activity.Running && current == Activity.Idle ? StatusSound.Done : null;
                Check(StatusSounds.ForTransition(previous, current) == expected,
                    $"Sound selection for {previous} -> {current}");
            }
            Check(StatusSounds.FolderName(StatusSound.Run) == "run" &&
                StatusSounds.FolderName(StatusSound.Question) == "question" &&
                StatusSounds.FolderName(StatusSound.Done) == "done",
                "Each cue uses its own user-configurable sound folder");
            using var sounds = new StatusSounds(fixtureRoot);
            sounds.Play(StatusSound.Done);
            Check(sounds.Voices.Count == 0 && sounds.Warning is null,
                "Missing optional sound folders are silent without warnings");
            foreach (var sound in Enum.GetValues<StatusSound>())
            {
                var folder = StatusSounds.FolderName(sound);
                Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", folder, ".gitkeep")),
                    $"{folder} placeholder is included in build output");
                Directory.CreateDirectory(Path.Combine(fixtureRoot, folder));
                File.WriteAllText(Path.Combine(fixtureRoot, folder, ".gitkeep"), "");
                File.WriteAllText(Path.Combine(fixtureRoot, folder, "ignored.mp3"), "not a WAV");
                sounds.Play(sound);
                Check(sounds.Voices.Count == 0 && sounds.Warning is null,
                    $"{folder} is silent when it contains no WAV files");
            }
            var runPath = Path.Combine(fixtureRoot, "run", "any start sound.WAV");
            var questionPath = Path.Combine(fixtureRoot, "question", "user-question.wav");
            var donePath = Path.Combine(fixtureRoot, "done", "finished.wav");
            WriteAudioFixture(runPath);
            WriteAudioFixture(questionPath);
            WriteAudioFixture(donePath);
            sounds.Play(StatusSound.Run);
            sounds.Play(StatusSound.Run);
            sounds.Play(StatusSound.Question);
            sounds.Play(StatusSound.Done);
            var voices = sounds.Voices.ToArray();
            Check(voices.Length == 4 && voices.Distinct().Count() == 4 &&
                voices.All(voice => voice.Volume == 0.3),
                "Simultaneous cues and repeated identical cues retain independent quiet playback voices");
            PumpUntil(() => sounds.Warning is not null ||
                voices.All(voice => voice.NaturalDuration.HasTimeSpan &&
                    voice.Position >= TimeSpan.FromMilliseconds(30)), 10000);
            Check(sounds.Warning is null && sounds.Voices.Count == 4 &&
                voices.All(voice => voice.Position >= TimeSpan.FromMilliseconds(30)),
                "Arbitrary filenames and uppercase WAV extensions decode and advance concurrently");
            PumpUntil(() => sounds.Warning is not null || sounds.Voices.Count == 0, 10000);
            Check(sounds.Warning is null && sounds.Voices.Count == 0,
                "Finished playback releases every voice");
            sounds.Play(StatusSound.Run);
            sounds.Dispose();
            Check(sounds.Voices.Count == 0, "Shutdown closes active playback voices");
            var rejected = false;
            try { sounds.Play(StatusSound.Done); }
            catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "Disposed playback rejects new cues");
            using var configured = new StatusSounds(fixtureRoot);
            var warnings = 0;
            configured.WarningChanged += () => warnings++;
            var duplicatePath = Path.Combine(fixtureRoot, "run", "duplicate.wav");
            WriteAudioFixture(duplicatePath);
            configured.Play(StatusSound.Run);
            Check(configured.Voices.Count == 0 &&
                configured.Warning?.Contains("exactly one WAV") == true && warnings == 1,
                "Multiple WAVs are rejected with a visible folder warning instead of arbitrary selection");
            File.Delete(duplicatePath);
            configured.Play(StatusSound.Run);
            PumpUntil(() => configured.Warning is null, 10000);
            Check(warnings == 2 && configured.Voices.Count == 1,
                "Removing the extra WAV restores playback without restarting");
            PumpUntil(() => configured.Voices.Count == 0, 10000);
            File.WriteAllText(questionPath, "not a WAV file");
            configured.Play(StatusSound.Question);
            PumpUntil(() => configured.Warning is not null, 10000);
            Check(configured.Voices.Count == 0 && configured.Warning?.Contains(questionPath) == true,
                "Invalid user recordings report a decoder warning and release their voice");
            configured.Play(StatusSound.Done);
            PumpUntil(() => configured.Warning is null, 10000);
            Check(warnings == 4, "Successful playback clears the warning and notifies the panel");
            PumpUntil(() => configured.Voices.Count == 0, 10000);
            File.Delete(donePath);
            configured.Play(StatusSound.Done);
            Check(configured.Voices.Count == 0 && configured.Warning is null,
                "Removing a recording disables its cue without a restart or warning");
            WriteAudioFixture(donePath);
            configured.Play(StatusSound.Done);
            Check(configured.Voices.Count == 1, "Adding a recording enables its cue without a restart");
            PumpUntil(() => configured.Voices.Count == 0, 10000);
            Console.WriteLine($"Passed {checks} audio checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            app.Shutdown();
            if (Directory.Exists(fixtureRoot))
            {
                foreach (var folder in Directory.GetDirectories(fixtureRoot))
                {
                    foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
                    Directory.Delete(folder);
                }
                Directory.Delete(fixtureRoot);
            }
        }
    }

    private static void WriteAudioFixture(string path)
    {
        const int sampleRate = 44100;
        const int frames = sampleRate * 4 / 5;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 2);
        for (var i = 0; i < frames; i++)
        {
            var fade = Math.Min(1.0, Math.Min(i, frames - 1 - i) / 220.0);
            writer.Write((short)(800 * fade * Math.Sin(2 * Math.PI * 440 * i / sampleRate)));
        }
    }

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

    private static int RunRemoteLifecycleTests()
    {
        try
        {
            var source = new RemoteSource
            {
                Project = "vscdeck-lifecycle-test",
                Workspace = @"C:\vscdeck-lifecycle-test",
                DockerContext = "vscdeck-test-" + Guid.NewGuid().ToString("N")
            };
            var window = new CodeWindow(0, 0, "",
                $"{source.Project} [Dev Container: Test @ {source.DockerContext}]", null);
            using var monitor = new RemoteMonitor([source]);
            var closed = monitor.Scan([]);
            Check(closed.Warnings.Count == 0 && closed.Errors.Count == 0,
                "Closed remote workspaces do not start checks or report warnings");
            RemoteSnapshot WaitForError()
            {
                var timeout = Stopwatch.StartNew();
                RemoteSnapshot snapshot;
                do
                {
                    snapshot = monitor.Scan([window]);
                    if (snapshot.Errors.Count > 0) return snapshot;
                    Thread.Sleep(25);
                } while (timeout.Elapsed < TimeSpan.FromSeconds(70));
                throw new TimeoutException("Expected an explicit error from the nonexistent Docker context.");
            }
            var open = WaitForError();
            Check(open.Errors.ContainsKey(source.Identity) && open.Warnings.Count > 0,
                "Open remote workspaces still report genuine check failures");
            closed = monitor.Scan([]);
            Check(closed.Errors.Count == 0 && closed.Warnings.Count == 0 &&
                closed.Sessions.Count == 0 && closed.Available.Count == 0 &&
                closed.StatusBarBackgrounds.Count == 0,
                "Closing a remote window removes all cached source state");
            Check(monitor.Scan([window]).Errors.Count == 0,
                "Reopening a remote window starts without stale errors");
            Check(WaitForError().Errors.ContainsKey(source.Identity),
                "Reopening a remote window automatically resumes checks");
            Console.WriteLine($"All {checks} remote lifecycle checks passed.");
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
            "Workspace status bar color stripe");

    private static Border FocusMarker(Button button) =>
        (Border)button.Template.FindName("FocusMarker", button);

    private static Border ReorderGrip(Button button) =>
        ((StackPanel)VisualTreeHelper.GetParent(button)).Children.OfType<Border>().Single(border =>
            System.Windows.Automation.AutomationProperties.GetName(border) !=
            "Workspace status bar color stripe");

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
