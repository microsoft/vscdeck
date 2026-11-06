using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using VSCDeck.App;

namespace VSCDeck.WindowsTests;

internal static partial class Program
{
    private static int RunChatTests()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var layout = new StackPanel();
        var session = new ChatTestGroup("interactive-session chat-view-getting-started-disabled");
        var editor = new ChatTestGroup("monaco-editor");
        var input = new ChatTestInput();
        editor.Children.Add(input);
        session.Children.Add(editor);
        var toggle = new Button { Content = "Toggle Chat" };
        System.Windows.Automation.AutomationProperties.SetName(toggle, "Toggle Chat");
        var toggles = 0;
        toggle.Click += (_, _) =>
        {
            toggles++;
            session.Visibility = session.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        };
        layout.Children.Add(toggle);
        layout.Children.Add(new ChatTestInput());
        layout.Children.Add(session);
        var target = new Window
        {
            Title = "VSC Deck isolated chat test", Content = layout, Width = 400, Height = 300
        };
        try
        {
            target.Show();
            target.Activate();
            var handle = new WindowInteropHelper(target).Handle;
            PumpUntil(() => NativeWindows.GetForegroundWindow() == handle);

            string? Invoke(Func<string?> action)
            {
                var task = Task.Run(action);
                PumpUntil(() => task.IsCompleted, 10000);
                return task.GetAwaiter().GetResult();
            }

            void CheckOpenInput(string label)
            {
                Check(Invoke(() => NativeWindows.OpenCopilotChat(handle)) is null, label + " open request");
                PumpFor(100);
                Check(toggles == 0 && session.Visibility == Visibility.Visible,
                    label + " keeps the panel open");
                Check(Invoke(() => NativeWindows.FocusCopilotChatInput(handle)) is null,
                    label + " focus request");
                PumpUntil(() => input.IsKeyboardFocused);
                Check(input.IsKeyboardFocused, label + " focuses chat rather than the unrelated editor");
            }

            CheckOpenInput("Original input ancestry");
            session.Children.Clear();
            var wrapper = new ChatTestGroup("chat-input-container");
            wrapper.Children.Add(editor);
            session.Children.Add(wrapper);
            CheckOpenInput("Wrapped input ancestry");
            Check(NativeWindows.Activate(handle) is null, "Maximize request");
            PumpUntil(() => NativeWindows.IsZoomed(handle));
            CheckOpenInput("Maximized wrapped input");
            Check(NativeWindows.Activate(handle) is null, "Restore request");
            PumpUntil(() => !NativeWindows.IsZoomed(handle));
            CheckOpenInput("Restored wrapped input");

            editor.Children.Clear();
            Check(Invoke(() => NativeWindows.OpenCopilotChat(handle)) is null,
                "Visible chat without a rendered input is not toggled");
            PumpFor(100);
            Check(toggles == 0 && session.Visibility == Visibility.Visible,
                "Missing input does not collapse a visible panel");
            Check(Invoke(() => NativeWindows.FocusCopilotChatInput(handle)) is not null,
                "Missing chat input reports a warning instead of focusing an unrelated editor");

            editor.Children.Add(input);
            session.Visibility = Visibility.Collapsed;
            PumpFor(100);
            Check(Invoke(() => NativeWindows.OpenCopilotChat(handle)) is null, "Closed chat open request");
            PumpUntil(() => toggles == 1 && session.Visibility == Visibility.Visible);
            Check(Invoke(() => NativeWindows.OpenCopilotChat(handle)) is null, "Repeated open request");
            PumpFor(100);
            Check(toggles == 1, "Repeated activation opens a closed panel only once");
            Check(Invoke(() => NativeWindows.FocusCopilotChatInput(handle)) is null,
                "Reopened chat input focus request");
            PumpUntil(() => input.IsKeyboardFocused);
            Console.WriteLine($"All {checks} chat checks passed without interacting with VS Code or saved settings.");
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

    private sealed class ChatTestGroup(string className) : StackPanel
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new ChatTestGroupPeer(this, className);
    }

    private sealed class ChatTestGroupPeer(ChatTestGroup owner, string className)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => className;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    }

    private sealed class ChatTestInput : TextBox
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new ChatTestInputPeer(this);
    }

    private sealed class ChatTestInputPeer(ChatTestInput owner) : TextBoxAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => "native-edit-context";
    }
}
