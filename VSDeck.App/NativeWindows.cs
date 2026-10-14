using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VSDeck.Core;

namespace VSDeck.App;

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
}
