using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.IO;
using System.Security;
using VSCDeck.Core;

namespace VSCDeck.App;

public sealed class SettingsWindow : Window
{
    private readonly ComboBox view;
    private readonly CheckBox snapToScreenEdges;
    private readonly CheckBox notificationArea;
    private readonly CheckBox startWithWindows;
    private readonly TextBox shortcuts;
    private readonly TextBlock sourceStatus;
    private List<RemoteSource> remoteSources;
    public int SelectedSize { get; private set; }
    public bool SelectedSnapToScreenEdges { get; private set; }
    public bool SelectedUseNotificationArea { get; private set; }
    public bool SelectedStartWithWindows { get; private set; }
    public string SelectedHotkeys { get; private set; } = "";
    public List<RemoteSource> SelectedRemoteSources { get; private set; } = [];

    public SettingsWindow(Settings settings)
    {
        Title = "VSC Deck settings";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = true;
        remoteSources = settings.RemoteSources.ToList();
        var panel = new StackPanel { Margin = new(16) };
        panel.Children.Add(new TextBlock { Text = "View" });
        view = new ComboBox { ItemsSource = new[] { "Large", "Compact" },
            SelectedItem = settings.ButtonSize == 64 ? "Large" : "Compact", Margin = new(0, 6, 0, 16) };
        panel.Children.Add(view);
        snapToScreenEdges = new CheckBox { Content = "Snap to screen edges",
            IsChecked = settings.SnapToScreenEdges, Margin = new(0, 0, 0, 16) };
        panel.Children.Add(snapToScreenEdges);
        notificationArea = new CheckBox
        {
            Content = "Show in notification area instead of the taskbar",
            IsChecked = settings.UseNotificationArea,
            Margin = new(0, 0, 0, 8)
        };
        panel.Children.Add(notificationArea);
        startWithWindows = new CheckBox
        {
            Content = "Start VSC Deck when I sign in to Windows",
            IsChecked = settings.StartWithWindows,
            Margin = new(0, 0, 0, 16)
        };
        panel.Children.Add(startWithWindows);
        panel.Children.Add(new TextBlock { Text = "Global hotkeys, in button-position order (comma separated)" });
        shortcuts = new TextBox { Text = settings.Hotkeys, TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false, MinHeight = 65, Margin = new(0, 6, 0, 8) };
        panel.Children.Add(shortcuts);
        var discover = new Button { Content = "Discover Docker/SSH sources",
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new(8, 3, 8, 3), Margin = new(0, 8, 0, 4) };
        sourceStatus = new TextBlock { Text = SourceStatusText(), TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 0, 0, 8) };
        discover.Click += async (_, _) =>
        {
            discover.IsEnabled = false;
            sourceStatus.Text = "Discovering running dev containers...";
            try
            {
                var result = await RemoteSourceDiscovery.Discover();
                remoteSources = result.Sources.Concat(remoteSources)
                    .GroupBy(source => source.Identity, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).ToList();
                sourceStatus.Text = SourceStatusText(result.Sources.Count, result.Warnings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Text.Json.JsonException or FormatException or Win32Exception)
            {
                sourceStatus.Text = "Discovery failed: " + ex.Message;
                AppStorage.Log(sourceStatus.Text);
            }
            finally { discover.IsEnabled = true; }
        };
        panel.Children.Add(discover);
        panel.Children.Add(sourceStatus);
        panel.Children.Add(new TextBlock
        {
            Text = "Use Ctrl, Alt, Shift, or Win plus a digit, letter, or F1-F24 (except F12).\n" +
                "Hotkeys follow positions when buttons are reordered.\n" +
                "A background window is activated. A foreground window toggles maximize/restore.\n\n" +
                "Green: idle/done | Blue: running | Yellow: question\nGray: no supported status source / read error\n\n" +
                "Hover a button for all sessions, activity descriptions, and pending questions.",
            TextWrapping = TextWrapping.Wrap
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 75 };
        save.Click += (_, _) =>
        {
            try
            {
                _ = Hotkey.ParseList(shortcuts.Text);
                var selectedStartWithWindows = startWithWindows.IsChecked == true;
                WindowsStartup.SetEnabled(selectedStartWithWindows);
                SelectedSize = Equals(view.SelectedItem, "Compact") ? 32 : 64;
                SelectedSnapToScreenEdges = snapToScreenEdges.IsChecked == true;
                SelectedUseNotificationArea = notificationArea.IsChecked == true;
                SelectedStartWithWindows = selectedStartWithWindows;
                SelectedHotkeys = shortcuts.Text.Trim();
                SelectedRemoteSources = remoteSources;
                DialogResult = true;
            }
            catch (FormatException ex) { MessageBox.Show(this, ex.Message, "Invalid hotkeys"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                SecurityException or InvalidOperationException)
            {
                MessageBox.Show(this, "Windows startup could not be updated:\n" + ex.Message,
                    "VSC Deck settings error");
            }
        };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        panel.Children.Add(actions);
        Content = panel;
    }

    private string SourceStatusText(int? discovered = null, IReadOnlyList<string>? warnings = null)
    {
        var text = $"{remoteSources.Count} Docker/SSH sources configured.";
        if (discovered is not null) text += $" Discovered {discovered} matching running containers.";
        if (warnings is { Count: > 0 }) text += "\n" + string.Join("\n", warnings);
        return text;
    }
}
