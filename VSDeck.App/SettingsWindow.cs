using System.Windows;
using System.Windows.Controls;
using VSDeck.Core;

namespace VSDeck.App;

public sealed class SettingsWindow : Window
{
    private readonly ComboBox view;
    private readonly CheckBox snapToScreenEdges;
    private readonly TextBox shortcuts;
    public int SelectedSize { get; private set; }
    public bool SelectedSnapToScreenEdges { get; private set; }
    public string SelectedHotkeys { get; private set; } = "";

    public SettingsWindow(Settings settings)
    {
        Title = "VSDeck settings";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = true;
        var panel = new StackPanel { Margin = new(16) };
        panel.Children.Add(new TextBlock { Text = "View" });
        view = new ComboBox { ItemsSource = new[] { "Large", "Compact" },
            SelectedItem = settings.ButtonSize == 64 ? "Large" : "Compact", Margin = new(0, 6, 0, 16) };
        panel.Children.Add(view);
        snapToScreenEdges = new CheckBox { Content = "Snap to screen edges",
            IsChecked = settings.SnapToScreenEdges, Margin = new(0, 0, 0, 16) };
        panel.Children.Add(snapToScreenEdges);
        panel.Children.Add(new TextBlock { Text = "Global hotkeys, in button-position order (comma separated)" });
        shortcuts = new TextBox { Text = settings.Hotkeys, TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false, MinHeight = 65, Margin = new(0, 6, 0, 8) };
        panel.Children.Add(shortcuts);
        panel.Children.Add(new TextBlock
        {
            Text = "Use Ctrl, Alt, Shift, or Win plus a digit, letter, or F1-F24 (except F12).\n" +
                "Hotkeys follow positions when buttons are reordered.\n" +
                "A background window is activated. A foreground window toggles maximize/restore.\n\n" +
                "Green: idle | Blue: running | Flashing yellow: question\nGray: no supported status source / read error\n\n" +
                "Hover a button for all sessions, activity descriptions, and pending questions.\n" +
                $"{settings.RemoteSources.Count} Docker/SSH sources configured in settings.json.",
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
                SelectedSize = Equals(view.SelectedItem, "Compact") ? 32 : 64;
                SelectedSnapToScreenEdges = snapToScreenEdges.IsChecked == true;
                SelectedHotkeys = shortcuts.Text.Trim();
                DialogResult = true;
            }
            catch (FormatException ex) { MessageBox.Show(this, ex.Message, "Invalid hotkeys"); }
        };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        panel.Children.Add(actions);
        Content = panel;
    }
}
