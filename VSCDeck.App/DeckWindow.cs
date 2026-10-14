using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using System.Windows.Threading;
using VSCDeck.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace VSCDeck.App;

public sealed class DeckWindow : Window
{
    private const int SnapThresholdPixels = 8;
    internal const int RunningPulseCycleMilliseconds = 1200;
    internal const int BlipOnMilliseconds = 50;
    internal const int CompletionBlipCycleMilliseconds = 2000;
    internal const int QuestionBlipCycleMilliseconds = 1500;
    internal static Color IdleLampColor { get; } = Color.FromRgb(12, 68, 34);
    internal static Color RunningLampColor { get; } = Color.FromRgb(16, 55, 126);
    internal static Color QuestionLampColor { get; } = Color.FromRgb(75, 52, 5);
    private readonly Settings settings;
    private CopilotMonitor monitor;
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, AllowDrop = true };
    private readonly TextBlock notice = new() { Foreground = Brushes.Gold, FontSize = 10, Margin = new(6, 0, 6, 3), MaxWidth = 520 };
    private readonly DockPanel header;
    private readonly ScrollViewer scroll;
    private readonly Button compactModeToggle;
    private readonly Border compactDragHandle;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly List<Tile> tiles = [];
    private readonly List<int> registered = [];
    private readonly Dictionary<int, nint> hotkeyTargets = [];
    private static readonly ControlTemplate CaptionButtonTemplate = CreateCaptionButtonTemplate(
        Color.FromRgb(63, 69, 79), Color.FromRgb(76, 83, 94));
    private static readonly ControlTemplate CloseButtonTemplate = CreateCaptionButtonTemplate(
        Color.FromRgb(196, 43, 28), Color.FromRgb(160, 35, 24));
    private static readonly ControlTemplate TileTemplate = CreateTileTemplate();
    private List<Hotkey> hotkeys;
    private HwndSource? source;
    private InsertionAdorner? reorderMarker;
    private Forms.NotifyIcon? notificationIcon;
    private Drawing.Icon? notificationIconImage;
    private Forms.ToolStripMenuItem? notificationVisibilityItem;
    private bool scanning;
    private bool closing;
    private string hotkeyWarning = "";
    private string actionWarning = "";
    private string hotkeySignature = "";
    internal int? ReorderInsertionIndex { get; private set; }
    internal bool IsReorderMarkerVisible => reorderMarker?.Visibility == Visibility.Visible;
    internal bool IsNotificationIconVisible => notificationIcon?.Visible == true;
    internal IReadOnlyList<string> NotificationMenuItems =>
        notificationIcon?.ContextMenuStrip?.Items.OfType<Forms.ToolStripItem>()
            .Select(item => item.Text).Where(text => !string.IsNullOrEmpty(text))
            .Select(text => text!).ToArray() ?? [];

    public DeckWindow(Settings settings)
    {
        this.settings = settings;
        monitor = new CopilotMonitor(settings.RemoteSources);
        hotkeys = Hotkey.ParseList(settings.Hotkeys);
        Title = "VSC Deck - Copilot overview";
        Icon = BitmapFrame.Create(new Uri(
            "pack://application:,,,/VSCDeck;component/Assets/VSCDeck.ico"));
        ShowInTaskbar = !settings.UseNotificationArea;
        Topmost = true;
        ShowActivated = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = new SolidColorBrush(Color.FromRgb(27, 31, 39));
        Foreground = Brushes.White;
        MaxWidth = SystemParameters.PrimaryScreenWidth - 24;
        Left = settings.Left ?? Math.Max(12, SystemParameters.PrimaryScreenWidth - 360);
        Top = settings.Top ?? 24;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(40, 45, 55)) };
        var close = CreateCaptionButton("×", "Close VSC Deck", CloseButtonTemplate);
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var options = CreateCaptionButton("⚙", "Settings", CaptionButtonTemplate);
        options.Click += async (_, _) => await OpenSettings();
        DockPanel.SetDock(options, Dock.Right);
        header.Children.Add(options);
        var compact = CreateCaptionButton("↙", "Switch to compact view", CaptionButtonTemplate);
        compact.Click += (_, _) => SwitchToCompactMode();
        DockPanel.SetDock(compact, Dock.Right);
        header.Children.Add(compact);
        var caption = new TextBlock { Text = "VSC Deck  ::", FontSize = 12, Margin = new(8, 5, 16, 5),
            Cursor = Cursors.SizeAll };
        caption.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount != 1) return;
            DragMove();
            SnapAndSavePosition();
        };
        header.Children.Add(caption);
        Grid.SetColumnSpan(header, 3);
        root.Children.Add(header);
        scroll = new ScrollViewer { Content = buttons, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(3) };
        var adornerDecorator = new AdornerDecorator { Child = scroll };
        Grid.SetRow(adornerDecorator, 1);
        root.Children.Add(adornerDecorator);
        buttons.DragOver += ButtonsDragOver;
        buttons.DragLeave += ButtonsDragLeave;
        buttons.Drop += ButtonsDrop;
        compactModeToggle = new Button { Content = "↗", Padding = new(0), Margin = new(0, 3, 0, 3),
            BorderThickness = new(0), Background = Brushes.Transparent, Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 14,
            Template = CaptionButtonTemplate, VerticalAlignment = VerticalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(compactModeToggle, "Switch to large view");
        compactModeToggle.Click += (_, _) => SwitchToLargeMode();
        Grid.SetRow(compactModeToggle, 1);
        Grid.SetColumn(compactModeToggle, 1);
        root.Children.Add(compactModeToggle);
        compactDragHandle = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(54, 60, 70)),
            Cursor = Cursors.SizeAll,
            Child = new TextBlock
            {
                Text = "⋮",
                Foreground = Brushes.LightGray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(compactDragHandle, "Move compact view");
        compactDragHandle.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount != 1) return;
            DragMove();
            SnapAndSavePosition();
        };
        Grid.SetColumn(compactDragHandle, 2);
        Grid.SetRowSpan(compactDragHandle, 3);
        root.Children.Add(compactDragHandle);
        Grid.SetRow(notice, 2);
        Grid.SetColumnSpan(notice, 2);
        root.Children.Add(notice);
        Content = root;
        UpdateNotificationAreaMode();
        UpdateViewMode();
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(WindowMessages);
        };
        Loaded += async (_, _) =>
        {
            ClampPosition();
            timer.Start();
            await Refresh();
        };
        DpiChanged += (_, _) => { UpdateViewMode(); BuildTiles(); ClampPosition(); };
        timer.Tick += async (_, _) =>
        {
            AcknowledgeForegroundTile();
            PaintTiles();
            await Refresh();
        };
        Closing += (_, _) =>
        {
            closing = true;
            timer.Stop();
            monitor.Dispose();
            UnregisterHotkeys();
            SavePosition();
            source?.RemoveHook(WindowMessages);
            DisposeNotificationIcon();
        };
    }

    private nint WindowMessages(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x21)
        {
            handled = true;
            return 3; // MA_NOACTIVATE keeps button clicks from stealing focus before activation/toggle.
        }
        if (message == 0x312 && hotkeyTargets.TryGetValue((int)wParam, out var target))
        {
            _ = Activate(target);
            handled = true;
        }
        return 0;
    }

    private async Task Refresh()
    {
        if (scanning || closing) return;
        scanning = true;
        try
        {
            var snapshot = await Task.Run(monitor.Scan);
            if (closing) return;
            var oldSignature = string.Join("|", tiles.Select(t => $"{t.Window.Handle}:{t.Window.Identity}"));
            tiles.RemoveAll(t => snapshot.Windows.All(w => w.Handle != t.Window.Handle));
            foreach (var window in snapshot.Windows)
            {
                var existing = tiles.Find(t => t.Window.Handle == window.Handle);
                if (existing is null) tiles.Add(new Tile(window));
                else existing.Window = window;
            }
            var sorted = Identity.Sort(tiles, settings.Order, t => t.Window.Identity);
            tiles.Clear();
            tiles.AddRange(sorted);
            foreach (var tile in tiles)
            {
                var workspace = tile.Window.Workspace;
                var sessions = workspace is null ? [] :
                    snapshot.Sessions.Where(s => s.Workspace.Equals(workspace, StringComparison.OrdinalIgnoreCase)).ToArray();
                snapshot.WorkspaceErrors.TryGetValue(workspace ?? "", out var error);
                var status = WindowStatus.Aggregate(sessions, error,
                    workspace is not null && snapshot.AvailableWorkspaces.Contains(workspace));
                tile.CompletionPending = UpdateCompletionPending(tile.CompletionPending,
                    tile.Status.Activity, status.Activity,
                    NativeWindows.GetForegroundWindow() == tile.Window.Handle);
                tile.Status = status;
            }
            if (oldSignature != string.Join("|", tiles.Select(t => $"{t.Window.Handle}:{t.Window.Identity}")) ||
                buttons.Children.Count == 0)
            {
                BuildTiles();
                RegisterHotkeys();
            }
            PaintTiles();
            UpdateNotice(snapshot.Warnings);
            AppStorage.WriteDiagnostic(new
            {
                UpdatedAt = DateTimeOffset.Now,
                ButtonPixels = settings.ButtonSize,
                PanelHandle = new WindowInteropHelper(this).Handle.ToInt64(),
                Windows = tiles.Select((t, index) => new
                {
                    Position = index + 1, Handle = t.Window.Handle.ToInt64(), t.Window.ProcessId,
                    t.Window.Project, t.Window.Workspace, Identity = t.Window.Identity,
                    Activity = t.Status.Activity.ToString(),
                    t.CompletionPending,
                    Hotkey = index < hotkeys.Count ? hotkeys[index].Label : "Unassigned",
                    Registered = hotkeyTargets.ContainsValue(t.Window.Handle),
                    PixelWidth = t.Button is null ? 0 : t.Button.ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX,
                    PixelHeight = t.Button is null ? 0 : t.Button.ActualHeight * VisualTreeHelper.GetDpi(this).DpiScaleY,
                    Sessions = t.Status.Sessions.Select(s => new { s.Id, Activity = s.Activity.ToString(), s.Title, s.Description }),
                    t.Status.Detail
                }),
                Warnings = snapshot.Warnings.Concat(new[] { hotkeyWarning, actionWarning, AppStorage.LoggingWarning ?? "" }).Where(s => s.Length > 0)
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or JsonException)
        {
            AppStorage.Log(ex.ToString());
            foreach (var tile in tiles) tile.Status = WindowStatus.Aggregate(tile.Status.Sessions, ex.Message);
            PaintTiles();
            notice.Text = "Monitor error - hover for details";
            notice.ToolTip = string.Join("\n", new[] { ex.Message, AppStorage.LoggingWarning }.Where(s => s is not null));
            notice.Visibility = Visibility.Visible;
        }
        finally { scanning = false; }
    }

    private void BuildTiles()
    {
        HideReorderMarker();
        UpdateViewMode();
        buttons.Children.Clear();
        if (tiles.Count == 0)
        {
            buttons.Children.Add(new TextBlock { Text = "No VS Code windows found", Margin = new(8), Foreground = Brushes.LightGray });
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        foreach (var tile in tiles)
        {
            var column = new StackPanel { Margin = new(2, 0, 2, 0) };
            tile.Label = new TextBlock { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, FontSize = settings.ButtonSize == 32 ? 8 : 10,
                Padding = new(settings.ButtonSize == 32 ? 0 : 2), ClipToBounds = true };
            tile.Label.FontSize /= dpi.DpiScaleY;
            tile.Button = new Button
            {
                Content = tile.Label, Width = settings.ButtonSize / dpi.DpiScaleX,
                Height = settings.ButtonSize / dpi.DpiScaleY, Padding = new(0), Margin = new(0),
                BorderThickness = new(1),
                BorderBrush = new LinearGradientBrush(Color.FromArgb(150, 255, 255, 255),
                    Color.FromArgb(45, 200, 218, 240), 90),
                Cursor = Cursors.Hand,
                Template = TileTemplate,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            System.Windows.Automation.AutomationProperties.SetName(tile.Button, tile.Window.Project);
            tile.Button.Click += async (_, _) => await Activate(tile.Window.Handle);
            column.Children.Add(tile.Button);
            if (settings.ButtonSize == 64)
            {
                var grip = new Border { Background = new SolidColorBrush(Color.FromRgb(54, 60, 70)),
                    Margin = new(0, 2, 0, 0), Height = 15, Cursor = Cursors.SizeWE,
                    Child = new TextBlock { Text = "::::", FontSize = 10, Foreground = Brushes.LightGray,
                        HorizontalAlignment = HorizontalAlignment.Center } };
                Point start = default;
                grip.MouseLeftButtonDown += (_, e) => { start = e.GetPosition(grip); };
                grip.MouseMove += (_, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed) return;
                    var point = e.GetPosition(grip);
                    if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                        Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                    try
                    {
                        DragDrop.DoDragDrop(grip, new DataObject("VSCDeck.Tile", tile.Window.Handle.ToInt64()),
                            DragDropEffects.Move);
                    }
                    finally { HideReorderMarker(); }
                };
                column.Children.Add(grip);
            }
            buttons.Children.Add(column);
        }
        PaintTiles();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ClampPosition));
    }

    private void ButtonsDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("VSCDeck.Tile"))
        {
            e.Effects = DragDropEffects.None;
            HideReorderMarker();
            e.Handled = true;
            return;
        }
        if (e.Data.GetData("VSCDeck.Tile") is not long handle) return;
        var from = tiles.FindIndex(tile => tile.Window.Handle.ToInt64() == handle);
        if (from < 0) return;
        var columns = buttons.Children.OfType<FrameworkElement>()
            .Where((_, index) => index != from).ToArray();
        if (columns.Length == 0)
        {
            HideReorderMarker();
            return;
        }
        var centers = columns.Select(column =>
            column.TranslatePoint(new Point(column.ActualWidth / 2, 0), buttons).X).ToArray();
        var index = CalculateInsertionIndex(centers, e.GetPosition(buttons).X);
        if (index == from)
        {
            HideReorderMarker();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }
        ShowReorderMarker(columns, index);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ButtonsDragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(buttons);
        if (point.X < 0 || point.Y < 0 || point.X > buttons.ActualWidth || point.Y > buttons.ActualHeight)
            HideReorderMarker();
    }

    private void ButtonsDrop(object sender, DragEventArgs e)
    {
        var insertion = ReorderInsertionIndex;
        HideReorderMarker();
        if (insertion is null || e.Data.GetData("VSCDeck.Tile") is not long handle) return;
        var from = tiles.FindIndex(tile => tile.Window.Handle.ToInt64() == handle);
        if (from < 0) return;
        var to = insertion.Value;
        if (from == to)
        {
            e.Handled = true;
            return;
        }
        var moving = tiles[from];
        tiles.RemoveAt(from);
        tiles.Insert(to, moving);
        settings.Order = Identity.RememberOrder(tiles.Select(tile => tile.Window.Identity), settings.Order);
        SaveSettings();
        BuildTiles();
        RegisterHotkeys();
        PaintTiles();
        e.Handled = true;
    }

    private void ShowReorderMarker(IReadOnlyList<FrameworkElement> columns, int insertion)
    {
        if (columns.Count == 0) return;
        var x = insertion switch
        {
            0 => columns[0].TranslatePoint(new Point(0, 0), buttons).X,
            _ when insertion == columns.Count =>
                columns[^1].TranslatePoint(new Point(columns[^1].ActualWidth, 0), buttons).X,
            _ => (columns[insertion - 1].TranslatePoint(
                    new Point(columns[insertion - 1].ActualWidth, 0), buttons).X +
                columns[insertion].TranslatePoint(new Point(0, 0), buttons).X) / 2
        };
        if (reorderMarker is null)
        {
            var layer = AdornerLayer.GetAdornerLayer(buttons);
            if (layer is null) return;
            reorderMarker = new InsertionAdorner(buttons);
            layer.Add(reorderMarker);
        }
        ReorderInsertionIndex = insertion;
        reorderMarker.Show(x);
    }

    private void HideReorderMarker()
    {
        ReorderInsertionIndex = null;
        reorderMarker?.Hide();
    }

    internal static int CalculateInsertionIndex(IReadOnlyList<double> centers, double x)
    {
        var index = 0;
        while (index < centers.Count && x >= centers[index]) index++;
        return index;
    }

    private void PaintTiles()
    {
        for (var index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];
            if (tile.Button is null || tile.Label is null) continue;
            tile.Button.Background = CreateGlassBrush(tile.Status.Activity switch
            {
                Activity.Idle => IdleLampColor,
                Activity.Running => RunningLampColor,
                Activity.Question => QuestionLampColor,
                _ => Color.FromRgb(87, 94, 106)
            });
            SetRunningPulse(tile.Button, tile.Status.Activity == Activity.Running);
            SetCompletionBlip(tile.Button, tile.CompletionPending);
            SetQuestionBlip(tile.Button, tile.Status.Activity == Activity.Question);
            tile.Label.Foreground = Brushes.White;
            var active = tile.Status.Sessions.Where(s => s.Activity is Activity.Running or Activity.Question).ToArray();
            var stateText = GetStateText(tile.Status.Activity, settings.ButtonSize == 32,
                tile.CompletionPending);
            if (settings.ButtonSize == 32)
                tile.Label.Text = $"{index + 1}\n{stateText}{(active.Length > 1 ? active.Length.ToString() : "")}";
            else
            {
                var description = active.Length == 0 ? "" : "\n" + string.Join("\n",
                    active.Take(2).Select(s => SessionTracker.Shorten(s.Title, 20)));
                if (active.Length > 2) description += $"\n+{active.Length - 2} more";
                tile.Label.Text = $"{index + 1}: {SessionTracker.Shorten(tile.Window.Project, 12)}\n{stateText}{description}";
            }

            var hotkey = index < hotkeys.Count ? hotkeys[index].Label : "No hotkey assigned";
            var shared = tiles.Count(t => t.Window.Identity == tile.Window.Identity) > 1
                ? "\nMultiple windows share this workspace; session status is workspace-level." : "";
            tile.Button.ToolTip = new TextBlock
            {
                Text = $"{tile.Window.Title}\n{tile.Window.Workspace ?? "Workspace not identified"}\n{hotkey}{shared}\n\n{tile.Status.Detail}",
                MaxWidth = 520, TextWrapping = TextWrapping.Wrap
            };
            ToolTipService.SetShowDuration(tile.Button, 60000);
            System.Windows.Automation.AutomationProperties.SetHelpText(tile.Button, $"{stateText}. {tile.Status.Detail}");
        }
    }

    private static ControlTemplate CreateTileTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "TileBorder" };
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var parent = new RelativeSource(RelativeSourceMode.TemplatedParent);
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = parent });
        border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = parent });
        border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = parent });
        var layers = new FrameworkElementFactory(typeof(Grid));
        var sheen = new FrameworkElementFactory(typeof(Border)) { Name = "GlassSheen" };
        sheen.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        sheen.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
        sheen.SetValue(IsHitTestVisibleProperty, false);
        var sheenBrush = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(52, 255, 255, 255), 0),
            new(Color.FromArgb(12, 255, 255, 255), .35),
            new(Colors.Transparent, .58)
        }, new Point(0, 0), new Point(0, 1));
        sheenBrush.Freeze();
        sheen.SetValue(Border.BackgroundProperty, sheenBrush);
        sheen.SetValue(Border.BorderThicknessProperty, new Thickness(0, 1, 0, 0));
        sheen.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)));
        layers.AppendChild(sheen);

        var runningGlow = new FrameworkElementFactory(typeof(Border)) { Name = "RunningGlow" };
        runningGlow.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        runningGlow.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
        runningGlow.SetValue(IsHitTestVisibleProperty, false);
        runningGlow.SetValue(OpacityProperty, 0.0);
        var lampBrush = new RadialGradientBrush
        {
            Center = new Point(.5, .45),
            GradientOrigin = new Point(.5, .45),
            RadiusX = .72,
            RadiusY = .72,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(225, 126, 208, 255), 0),
                new(Color.FromArgb(130, 45, 142, 255), .52),
                new(Colors.Transparent, 1)
            }
        };
        lampBrush.Freeze();
        runningGlow.SetValue(Border.BackgroundProperty, lampBrush);
        layers.AppendChild(runningGlow);

        var completionGlow = new FrameworkElementFactory(typeof(Border)) { Name = "CompletionGlow" };
        completionGlow.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        completionGlow.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
        completionGlow.SetValue(IsHitTestVisibleProperty, false);
        completionGlow.SetValue(OpacityProperty, 0.0);
        var completionBrush = new RadialGradientBrush
        {
            Center = new Point(.5, .45),
            GradientOrigin = new Point(.5, .45),
            RadiusX = .72,
            RadiusY = .72,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(240, 142, 255, 174), 0),
                new(Color.FromArgb(155, 37, 214, 94), .52),
                new(Colors.Transparent, 1)
            }
        };
        completionBrush.Freeze();
        completionGlow.SetValue(Border.BackgroundProperty, completionBrush);
        layers.AppendChild(completionGlow);

        var questionGlow = new FrameworkElementFactory(typeof(Border)) { Name = "QuestionGlow" };
        questionGlow.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        questionGlow.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
        questionGlow.SetValue(IsHitTestVisibleProperty, false);
        questionGlow.SetValue(OpacityProperty, 0.0);
        var questionBrush = new RadialGradientBrush
        {
            Center = new Point(.5, .45),
            GradientOrigin = new Point(.5, .45),
            RadiusX = .72,
            RadiusY = .72,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(245, 255, 240, 132), 0),
                new(Color.FromArgb(175, 255, 196, 45), .52),
                new(Colors.Transparent, 1)
            }
        };
        questionBrush.Freeze();
        questionGlow.SetValue(Border.BackgroundProperty, questionBrush);
        layers.AppendChild(questionGlow);

        var hoverLayer = new FrameworkElementFactory(typeof(Border)) { Name = "HoverLight" };
        hoverLayer.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        hoverLayer.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
        hoverLayer.SetValue(IsHitTestVisibleProperty, false);
        hoverLayer.SetValue(OpacityProperty, 0.0);
        hoverLayer.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)));
        layers.AppendChild(hoverLayer);

        var pressedLayer = new FrameworkElementFactory(typeof(Border)) { Name = "PressedShade" };
        pressedLayer.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        pressedLayer.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
        pressedLayer.SetValue(IsHitTestVisibleProperty, false);
        pressedLayer.SetValue(OpacityProperty, 0.0);
        pressedLayer.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(36, 0, 0, 0)));
        layers.AppendChild(pressedLayer);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = parent });
        presenter.SetBinding(FrameworkElement.HorizontalAlignmentProperty,
            new Binding("HorizontalContentAlignment") { RelativeSource = parent });
        presenter.SetBinding(FrameworkElement.VerticalAlignmentProperty,
            new Binding("VerticalContentAlignment") { RelativeSource = parent });
        layers.AppendChild(presenter);
        border.AppendChild(layers);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty,
            new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), "TileBorder"));
        hover.Setters.Add(new Setter(OpacityProperty, 1.0, "HoverLight"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(OpacityProperty, 1.0, "PressedShade"));
        pressed.Setters.Add(new Setter(OpacityProperty, 0.0, "HoverLight"));
        template.Triggers.Add(pressed);
        return template;
    }

    internal static void SetRunningPulse(Button button, bool running)
    {
        button.ApplyTemplate();
        if (button.Template.FindName("RunningGlow", button) is not Border glow) return;
        if (running == glow.HasAnimatedProperties) return;
        if (!running)
        {
            glow.BeginAnimation(OpacityProperty, null);
            glow.Opacity = 0;
            return;
        }
        var pulse = new DoubleAnimation
        {
            From = .08,
            To = .92,
            Duration = TimeSpan.FromMilliseconds(RunningPulseCycleMilliseconds / 2),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        glow.BeginAnimation(OpacityProperty, pulse);
    }

    internal static bool UpdateCompletionPending(bool pending, Activity previous,
        Activity current, bool isForeground) =>
        !isForeground && (pending || previous == Activity.Running && current == Activity.Idle);

    internal static string GetStateText(Activity activity, bool compact, bool completionPending) =>
        activity switch
        {
            Activity.Idle when compact && completionPending => "Done",
            Activity.Idle => "Idle",
            Activity.Running => compact ? "Run" : "Running",
            Activity.Question => compact ? "Ask" : "Question",
            _ => compact ? "?" : "Unknown"
        };

    internal static void SetCompletionBlip(Button button, bool enabled)
        => SetAttentionBlip(button, "CompletionGlow", enabled, CompletionBlipCycleMilliseconds);

    internal static void SetQuestionBlip(Button button, bool enabled)
        => SetAttentionBlip(button, "QuestionGlow", enabled, QuestionBlipCycleMilliseconds);

    private static void SetAttentionBlip(Button button, string elementName, bool enabled,
        int cycleMilliseconds)
    {
        button.ApplyTemplate();
        if (button.Template.FindName(elementName, button) is not Border glow) return;
        if (enabled == glow.HasAnimatedProperties) return;
        if (!enabled)
        {
            glow.BeginAnimation(OpacityProperty, null);
            glow.Opacity = 0;
            return;
        }
        var blip = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(cycleMilliseconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        blip.KeyFrames.Add(new DiscreteDoubleKeyFrame(.95, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blip.KeyFrames.Add(new DiscreteDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(BlipOnMilliseconds))));
        blip.KeyFrames.Add(new DiscreteDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(cycleMilliseconds))));
        glow.BeginAnimation(OpacityProperty, blip);
    }

    private static Button CreateCaptionButton(string content, string automationName, ControlTemplate template)
    {
        var button = new Button
        {
            Content = content,
            Width = 28,
            Height = 28,
            Padding = new(0),
            BorderThickness = new(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 14,
            Template = template
        };
        System.Windows.Automation.AutomationProperties.SetName(button, automationName);
        return button;
    }

    private static ControlTemplate CreateCaptionButtonTemplate(Color hoverColor, Color pressedColor)
    {
        var surface = new FrameworkElementFactory(typeof(Border)) { Name = "CaptionSurface" };
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("Content")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        surface.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hoverColor), "CaptionSurface"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(pressedColor), "CaptionSurface"));
        template.Triggers.Add(pressed);
        return template;
    }

    private static LinearGradientBrush CreateGlassBrush(Color color)
    {
        static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
        var brush = new LinearGradientBrush(new GradientStopCollection
        {
            new(Blend(color, Colors.White, .12), 0),
            new(color, .42),
            new(Blend(color, Colors.Black, .08), .64),
            new(Blend(color, Colors.Black, .24), 1)
        }, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private async Task Activate(nint target)
    {
        actionWarning = NativeWindows.Activate(target) ?? "";
        if (actionWarning.Length == 0)
        {
            for (var attempt = 0; attempt < 10 && NativeWindows.GetForegroundWindow() != target; attempt++)
                await Task.Delay(25);
            actionWarning = NativeWindows.OpenCopilotChat(target) ?? "";
            for (var attempt = 0; attempt < 10 && actionWarning.Length == 0; attempt++)
            {
                await Task.Delay(50);
                var focusWarning = NativeWindows.FocusCopilotChatInput(target);
                if (focusWarning is null) break;
                actionWarning = attempt == 9 ? focusWarning : "";
            }
        }
        AcknowledgeForegroundTile();
        PaintTiles();
        if (actionWarning.Length > 0) AppStorage.Log(actionWarning);
        UpdateNotice([]);
    }

    private void AcknowledgeForegroundTile()
    {
        var foreground = NativeWindows.GetForegroundWindow();
        var tile = tiles.Find(candidate => candidate.Window.Handle == foreground);
        if (tile is not null) tile.CompletionPending = false;
    }

    private void RegisterHotkeys()
    {
        if (source is null) return;
        var signature = settings.Hotkeys + "|" + string.Join(",", tiles.Select(t => t.Window.Handle));
        if (signature == hotkeySignature) return;
        hotkeySignature = signature;
        UnregisterHotkeys();
        var errors = new List<string>();
        for (var index = 0; index < Math.Min(tiles.Count, hotkeys.Count); index++)
        {
            var hotkey = hotkeys[index];
            var id = 100 + index;
            if (NativeWindows.RegisterHotKey(source.Handle, id, hotkey.Modifiers | 0x4000, hotkey.Key))
            {
                registered.Add(id);
                hotkeyTargets[id] = tiles[index].Window.Handle;
            }
            else errors.Add($"{hotkey.Label}: unavailable (Windows error {Marshal.GetLastWin32Error()})");
        }
        if (tiles.Count > hotkeys.Count) errors.Add("Some windows have no assigned hotkey. Add more in Settings.");
        hotkeyWarning = string.Join("\n", errors);
        if (hotkeyWarning.Length > 0) AppStorage.Log(hotkeyWarning);
    }

    private void UnregisterHotkeys()
    {
        if (source is not null)
            foreach (var id in registered)
                if (!NativeWindows.UnregisterHotKey(source.Handle, id))
                    AppStorage.Log($"Could not unregister hotkey {id}.");
        registered.Clear();
        hotkeyTargets.Clear();
    }

    private void UpdateNotice(IEnumerable<string> warnings)
    {
        var all = warnings.Concat(new[] { hotkeyWarning, actionWarning, AppStorage.LoggingWarning ?? "" }).Where(s => s.Length > 0).ToArray();
        notice.Visibility = all.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        notice.Text = AppStorage.LoggingWarning is not null ? "Logging warning - hover for details" :
            all.Length > 0 ? "Status / hotkey warning - hover for details" : "";
        notice.ToolTip = string.Join("\n", all);
    }

    private async Task OpenSettings()
    {
        timer.Stop();
        while (scanning) await Task.Delay(25);
        var dialog = new SettingsWindow(settings) { Owner = this };
        bool accepted;
        try { accepted = dialog.ShowDialog() == true; }
        finally { if (!closing) timer.Start(); }
        if (!accepted) return;
        settings.ButtonSize = dialog.SelectedSize;
        settings.SnapToScreenEdges = dialog.SelectedSnapToScreenEdges;
        settings.UseNotificationArea = dialog.SelectedUseNotificationArea;
        settings.StartWithWindows = dialog.SelectedStartWithWindows;
        settings.Hotkeys = dialog.SelectedHotkeys;
        settings.RemoteSources = dialog.SelectedRemoteSources;
        hotkeys = Hotkey.ParseList(settings.Hotkeys);
        monitor.Dispose();
        monitor = new CopilotMonitor(settings.RemoteSources);
        SaveSettings();
        UpdateNotificationAreaMode();
        BuildTiles();
        RegisterHotkeys();
        UpdateNotice([]);
        await Refresh();
    }

    private void UpdateNotificationAreaMode()
    {
        ShowInTaskbar = !settings.UseNotificationArea;
        if (!settings.UseNotificationArea)
        {
            DisposeNotificationIcon();
            return;
        }
        if (notificationIcon is not null) return;
        var resource = Application.GetResourceStream(new Uri(
            "pack://application:,,,/VSCDeck;component/Assets/VSCDeck.ico")) ??
            throw new IOException("The VSC Deck notification icon resource is unavailable.");
        using (resource.Stream)
        using (var icon = new Drawing.Icon(resource.Stream))
            notificationIconImage = (Drawing.Icon)icon.Clone();
        var menu = new Forms.ContextMenuStrip();
        notificationVisibilityItem = new Forms.ToolStripMenuItem("Hide VSC Deck");
        notificationVisibilityItem.Click += (_, _) =>
            Dispatcher.BeginInvoke(new Action(TogglePanelVisibility));
        var settingsItem = new Forms.ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            ShowPanel();
            await OpenSettings();
        }));
        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(Close));
        menu.Items.Add(notificationVisibilityItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        notificationIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = notificationIconImage,
            Text = "VSC Deck",
            Visible = true
        };
        notificationIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowPanel));
    }

    internal void TogglePanelVisibility()
    {
        if (IsVisible) Hide();
        else ShowPanel();
        if (notificationVisibilityItem is not null)
            notificationVisibilityItem.Text = IsVisible ? "Hide VSC Deck" : "Show VSC Deck";
    }

    private void ShowPanel()
    {
        if (!IsVisible) Show();
        if (notificationVisibilityItem is not null)
            notificationVisibilityItem.Text = "Hide VSC Deck";
    }

    private void DisposeNotificationIcon()
    {
        if (notificationIcon is not null)
        {
            notificationIcon.Visible = false;
            notificationIcon.ContextMenuStrip?.Dispose();
            notificationIcon.Dispose();
            notificationIcon = null;
        }
        notificationVisibilityItem = null;
        notificationIconImage?.Dispose();
        notificationIconImage = null;
    }

    private void SwitchToLargeMode()
    {
        settings.ButtonSize = 64;
        SaveSettings();
        BuildTiles();
        ClampPosition();
    }

    private void SwitchToCompactMode()
    {
        settings.ButtonSize = 32;
        SaveSettings();
        BuildTiles();
        ClampPosition();
    }

    private void UpdateViewMode()
    {
        var compact = settings.ButtonSize == 32;
        header.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        compactModeToggle.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        compactDragHandle.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        scroll.Margin = compact ? new Thickness(3, 3, 0, 3) : new Thickness(3);
        var dpi = VisualTreeHelper.GetDpi(this);
        compactModeToggle.MinWidth = 32 / dpi.DpiScaleX;
        compactDragHandle.Width = 16 / dpi.DpiScaleX;
    }

    private void ClampPosition()
    {
        Left = Math.Clamp(Left, SystemParameters.VirtualScreenLeft,
            Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth));
        Top = Math.Clamp(Top, SystemParameters.VirtualScreenTop,
            Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight));
    }

    private void SavePosition()
    {
        settings.Left = Left;
        settings.Top = Top;
        settings.Order = Identity.RememberOrder(tiles.Select(t => t.Window.Identity), settings.Order);
        SaveSettings();
    }

    private void SnapAndSavePosition()
    {
        ApplyScreenEdgeSnap(new WindowInteropHelper(this).Handle, settings.SnapToScreenEdges);
        SavePosition();
    }

    internal static NativeWindows.PixelOffset ApplyScreenEdgeSnap(nint hwnd, bool enabled) =>
        enabled ? NativeWindows.SnapToMonitorEdges(hwnd, SnapThresholdPixels) : default;

    private void SaveSettings()
    {
        try { AppStorage.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppStorage.Log(ex.ToString());
            MessageBox.Show(this, "Settings could not be saved:\n" + ex.Message, "VSC Deck settings error");
        }
    }

    private sealed class Tile(CodeWindow window)
    {
        public CodeWindow Window { get; set; } = window;
        public WindowStatus Status { get; set; } = WindowStatus.Aggregate([]);
        public Button? Button { get; set; }
        public TextBlock? Label { get; set; }
        public bool CompletionPending { get; set; }
    }

    private sealed class InsertionAdorner : Adorner
    {
        private double x;

        public InsertionAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
            Visibility = Visibility.Collapsed;
        }

        public void Show(double position)
        {
            x = position;
            Visibility = Visibility.Visible;
            InvalidateVisual();
        }

        public void Hide() => Visibility = Visibility.Collapsed;

        protected override void OnRender(DrawingContext drawingContext)
        {
            var marker = new SolidColorBrush(Color.FromRgb(0, 174, 255));
            marker.Freeze();
            drawingContext.DrawRoundedRectangle(marker, null,
                new Rect(x - 2, 0, 4, AdornedElement.RenderSize.Height), 2, 2);
        }
    }
}
