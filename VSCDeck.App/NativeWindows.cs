using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using VSCDeck.Core;

namespace VSCDeck.App;

public sealed record CodeWindow(nint Handle, int ProcessId, string Title, string Project, string? Workspace)
{
    public string Identity => Workspace ?? "title:" + Project.ToLowerInvariant();
}

internal static class NativeWindows
{
    private delegate bool EnumProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint hwnd, out PixelRect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PixelRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    internal readonly record struct PixelOffset(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public PixelRect Monitor;
        public PixelRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    public static List<CodeWindow> Discover()
    {
        var windows = new List<CodeWindow>();
        var names = new Dictionary<int, string>();
        if (!EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var rawPid);
            var pid = checked((int)rawPid);
            if (!names.TryGetValue(pid, out var name))
            {
                try { using var process = Process.GetProcessById(pid); name = process.ProcessName; }
                catch (ArgumentException) { return true; }
                catch (Win32Exception) { return true; }
                names[pid] = name;
            }
            if (!name.Equals("Code", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Code - Insiders", StringComparison.OrdinalIgnoreCase)) return true;
            var text = new StringBuilder(2048);
            GetWindowText(hwnd, text, text.Capacity);
            var title = text.ToString();
            if (!title.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase)) return true;
            windows.Add(new(hwnd, pid, title, Identity.ProjectFromTitle(title), null));
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to enumerate desktop windows.");
        return windows;
    }

    public static string? Activate(nint hwnd)
    {
        if (!IsWindow(hwnd)) return "This VS Code window has closed.";
        if (GetForegroundWindow() == hwnd)
        {
            ShowWindowAsync(hwnd, IsZoomed(hwnd) ? 9 : 3);
            return null;
        }
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9);
        if (SetForegroundWindow(hwnd)) return null;
        var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = hwnd, Flags = 3, Count = 3 };
        FlashWindowEx(ref flash);
        return "Windows denied foreground activation; the VS Code taskbar button has been flashed.";
    }

    internal static string? OpenCopilotChat(nint hwnd)
    {
        if (GetForegroundWindow() != hwnd)
            return "Copilot Chat was not opened because the selected VS Code window is not foreground.";
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (FindCopilotChatInput(root) is not null) return null;
            var buttons = root.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Toggle Chat")));
            foreach (AutomationElement button in buttons)
            {
                if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) ||
                    pattern is not InvokePattern invoke) continue;
                invoke.Invoke();
                return null;
            }
            return "Could not locate the VS Code Chat action.";
        }
        catch (ElementNotAvailableException ex)
        {
            return "Could not open Copilot Chat: " + ex.Message;
        }
    }

    internal static string? FocusCopilotChatInput(nint hwnd)
    {
        if (GetForegroundWindow() != hwnd)
            return "Copilot Chat input was not focused because the selected VS Code window is not foreground.";
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var input = FindCopilotChatInput(root);
            if (input is not null) { input.SetFocus(); return null; }
            return "Could not locate the Copilot Chat input after opening the Chat view.";
        }
        catch (ElementNotAvailableException ex)
        {
            return "Could not focus the Copilot Chat input: " + ex.Message;
        }
    }

    private static AutomationElement? FindCopilotChatInput(AutomationElement root)
    {
        var edits = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ClassNameProperty, "native-edit-context")));
        foreach (AutomationElement edit in edits)
        {
            var editor = TreeWalker.ControlViewWalker.GetParent(edit);
            var session = editor is null ? null : TreeWalker.ControlViewWalker.GetParent(editor);
            if (session?.Current.ClassName.StartsWith("interactive-session",
                StringComparison.Ordinal) == true) return edit;
        }
        return null;
    }

    internal static PixelOffset SnapToMonitorEdges(nint hwnd, int threshold)
    {
        var window = GetWindowBounds(hwnd);
        var offset = CalculateSnapOffset(window, GetMonitorBounds(hwnd), threshold);
        if (offset != default && !SetWindowPos(hwnd, 0, window.Left + offset.X, window.Top + offset.Y,
            0, 0, 0x0001 | 0x0004 | 0x0010))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to snap VSC Deck to the monitor edge.");
        return offset;
    }

    internal static PixelRect GetWindowBounds(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var bounds))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read the window bounds.");
        return bounds;
    }

    internal static PixelRect GetMonitorBounds(nint hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, 2);
        if (monitor == 0) throw new Win32Exception("Unable to identify the monitor containing the window.");
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read the monitor bounds.");
        return info.Monitor;
    }

    internal static PixelOffset CalculateSnapOffset(PixelRect window, PixelRect monitor, int threshold)
    {
        var x = Math.Abs(window.Left - monitor.Left) <= threshold ? monitor.Left - window.Left :
            Math.Abs(window.Right - monitor.Right) <= threshold ? monitor.Right - window.Right : 0;
        var y = Math.Abs(window.Top - monitor.Top) <= threshold ? monitor.Top - window.Top :
            Math.Abs(window.Bottom - monitor.Bottom) <= threshold ? monitor.Bottom - window.Bottom : 0;
        return new PixelOffset(x, y);
    }
}
