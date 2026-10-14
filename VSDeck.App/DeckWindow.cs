using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Threading;
using VSDeck.Core;

namespace VSDeck.App;

public sealed class DeckWindow : Window
{
    private readonly Settings settings;
    private readonly CopilotMonitor monitor;
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock notice = new() { Foreground = Brushes.Gold, FontSize = 10, Margin = new(6, 0, 6, 3), MaxWidth = 520 };
    private readonly DockPanel header;
    private readonly ScrollViewer scroll;
    private readonly Button compactModeToggle;
    private readonly Border compactDragHandle;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly List<Tile> tiles = [];
    private readonly List<int> registered = [];
    private readonly Dictionary<int, nint> hotkeyTargets = [];
    private static readonly ControlTemplate TileTemplate = CreateTileTemplate();
    private List<Hotkey> hotkeys;
    private HwndSource? source;
    private bool scanning;
    private bool closing;
    private bool blink;
    private string hotkeyWarning = "";
    private string actionWarning = "";
    private string hotkeySignature = "";

    public DeckWindow(Settings settings)
    {
        this.settings = settings;
        monitor = new CopilotMonitor(settings.RemoteSources);
        hotkeys = Hotkey.ParseList(settings.Hotkeys);
        Title = "VSDeck - Copilot overview";
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
        var close = new Button { Content = "X" };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var options = new Button { Content = "Settings" };
        options.Click += (_, _) => OpenSettings();
        DockPanel.SetDock(options, Dock.Right);
        header.Children.Add(options);
        var compact = new Button { Content = "↙" };
        System.Windows.Automation.AutomationProperties.SetName(compact, "Switch to compact view");
        compact.Click += (_, _) => SwitchToCompactMode();
        DockPanel.SetDock(compact, Dock.Right);
        header.Children.Add(compact);
        var caption = new TextBlock { Text = "VSDeck  ::", FontSize = 12, Margin = new(8, 5, 16, 5),
            Cursor = Cursors.SizeAll };
        caption.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount != 1) return;
            DragMove();
            SavePosition();
        };
        header.Children.Add(caption);
        Grid.SetColumnSpan(header, 3);
        root.Children.Add(header);
        scroll = new ScrollViewer { Content = buttons, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(3) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        compactModeToggle = new Button { Content = "↗", Padding = new(0), Margin = new(0, 3, 0, 3),
            VerticalAlignment = VerticalAlignment.Stretch };
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
            SavePosition();
        };
        Grid.SetColumn(compactDragHandle, 2);
        Grid.SetRowSpan(compactDragHandle, 3);
        root.Children.Add(compactDragHandle);
        Grid.SetRow(notice, 2);
        Grid.SetColumnSpan(notice, 2);
        root.Children.Add(notice);
        Content = root;
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
            blink = !blink;
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
            Activate(target);
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
                tile.Status = WindowStatus.Aggregate(sessions, error,
                    workspace is not null && snapshot.AvailableWorkspaces.Contains(workspace));
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
            var column = new StackPanel { Margin = new(2, 0, 2, 0), AllowDrop = true };
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
            tile.Button.Click += (_, _) => Activate(tile.Window.Handle);
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
                    DragDrop.DoDragDrop(grip, new DataObject("VSDeck.Tile", tile.Window.Handle.ToInt64()), DragDropEffects.Move);
                };
                column.Children.Add(grip);
            }
            column.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent("VSDeck.Tile") ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            column.Drop += (_, e) =>
            {
                if (e.Data.GetData("VSDeck.Tile") is not long handle) return;
                var from = tiles.FindIndex(t => t.Window.Handle.ToInt64() == handle);
                var to = tiles.IndexOf(tile);
                if (from < 0 || to < 0 || from == to) return;
                var moving = tiles[from];
                tiles.RemoveAt(from);
                tiles.Insert(to, moving);
                settings.Order = Identity.RememberOrder(tiles.Select(t => t.Window.Identity), settings.Order);
                SaveSettings();
                BuildTiles();
                RegisterHotkeys();
                PaintTiles();
                e.Handled = true;
            };
            buttons.Children.Add(column);
        }
        PaintTiles();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ClampPosition));
    }

    private void PaintTiles()
    {
        for (var index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];
            if (tile.Button is null || tile.Label is null) continue;
            tile.Button.Background = CreateGlassBrush(tile.Status.Activity switch
            {
                Activity.Idle => Color.FromRgb(32, 134, 72),
                Activity.Running => Color.FromRgb(32, 105, 201),
                Activity.Question => blink ? Color.FromRgb(255, 212, 59) : Color.FromRgb(174, 126, 12),
                _ => Color.FromRgb(87, 94, 106)
            });
            tile.Label.Foreground = tile.Status.Activity == Activity.Question ? Brushes.Black : Brushes.White;
            var active = tile.Status.Sessions.Where(s => s.Activity is Activity.Running or Activity.Question).ToArray();
            var stateText = tile.Status.Activity switch
            {
                Activity.Idle => "Idle",
                Activity.Running => settings.ButtonSize == 32 ? "Run" : "Running",
                Activity.Question => settings.ButtonSize == 32 ? "Ask" : "Question",
                _ => settings.ButtonSize == 32 ? "?" : "Unknown"
            };
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

    private void Activate(nint target)
    {
        actionWarning = NativeWindows.Activate(target) ?? "";
        if (actionWarning.Length > 0) AppStorage.Log(actionWarning);
        UpdateNotice([]);
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

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        settings.ButtonSize = dialog.SelectedSize;
        settings.Hotkeys = dialog.SelectedHotkeys;
        hotkeys = Hotkey.ParseList(settings.Hotkeys);
        SaveSettings();
        BuildTiles();
        RegisterHotkeys();
        UpdateNotice([]);
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

    private void SaveSettings()
    {
        try { AppStorage.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppStorage.Log(ex.ToString());
            MessageBox.Show(this, "Settings could not be saved:\n" + ex.Message, "VSDeck settings error");
        }
    }

    private sealed class Tile(CodeWindow window)
    {
        public CodeWindow Window { get; set; } = window;
        public WindowStatus Status { get; set; } = WindowStatus.Aggregate([]);
        public Button? Button { get; set; }
        public TextBlock? Label { get; set; }
    }
}
