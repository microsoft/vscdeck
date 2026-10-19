using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VSCDeck.Core;

namespace VSCDeck.App;

public sealed record MonitorSnapshot(List<CodeWindow> Windows, List<SessionStatus> Sessions,
    Dictionary<string, string> WorkspaceErrors, List<string> Warnings, HashSet<string> AvailableWorkspaces)
{
    public IReadOnlyList<RemoteSource> RemoteSources { get; init; } = [];
}

internal sealed class CopilotMonitor : IDisposable
{
    private readonly RemoteMonitor remoteMonitor;
    private readonly List<RemoteSource> remoteSources;
    private readonly RuntimeSourceDiscovery discovery = new();

    public CopilotMonitor(IEnumerable<RemoteSource>? sources = null)
    {
        remoteSources = sources?.ToList() ?? [];
        remoteMonitor = new RemoteMonitor(remoteSources);
    }

    public void Dispose() => remoteMonitor.Dispose();

    private readonly string sessionRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");
    private readonly Dictionary<string, SessionEventReader> eventFiles = [];
    private readonly HashSet<string> loggedErrors = [];

    public MonitorSnapshot Scan()
    {
        var warnings = new List<string>();
        var workspaceErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sessions = new List<SessionStatus>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(sessionRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(sessionRoot))
            {
                var metadataPath = Path.Combine(directory, "workspace.yaml");
                if (!File.Exists(metadataPath)) continue;
                var workspace = "";
                try
                {
                    var metadata = ReadMetadata(metadataPath);
                    workspace = Identity.NormalizePath(metadata.GetValueOrDefault("cwd", ""));
                    var client = metadata.GetValueOrDefault("client_name", "");
                    if (!client.Contains("vscode", StringComparison.OrdinalIgnoreCase)) continue;
                    if (workspace.Length > 0) paths.Add(workspace);
                    var filePath = Path.Combine(directory, "events.jsonl");
                    if (!File.Exists(filePath)) continue;
                    if (!eventFiles.TryGetValue(directory, out var events))
                        eventFiles[directory] = events = new SessionEventReader(Path.GetFileName(directory));
                    events.Tracker.Client = client;
                    events.Tracker.Title = SessionTracker.Shorten(metadata.GetValueOrDefault("name", "Copilot session"), 100);
                    events.Tracker.SetWorkspace(workspace);
                    events.Read(filePath);
                    if (IsLive(directory, events.Tracker.LastEvent) && !events.Tracker.Ended)
                    {
                        livePaths.Add(workspace);
                        sessions.Add(events.Tracker.Status);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or System.ComponentModel.Win32Exception)
                {
                    var message = $"Cannot read Copilot session {Path.GetFileName(directory)}: {ex.Message}";
                    warnings.Add(message);
                    if (workspace.Length > 0) workspaceErrors[workspace] = message;
                    LogOnce(message);
                }
            }
        }
        ReadIdePaths(paths, livePaths, warnings);
        var discoveredWindows = NativeWindows.Discover();
        var discovered = discovery.Poll(discoveredWindows, remoteSources, DateTimeOffset.UtcNow);
        remoteSources.AddRange(discovered.Sources);
        remoteMonitor.AddSources(discovered.Sources);
        warnings.AddRange(discovered.Warnings);
        var remote = remoteMonitor.Scan(discoveredWindows);
        sessions.AddRange(remote.Sessions);
        warnings.AddRange(remote.Warnings);
        foreach (var pair in remote.Errors) workspaceErrors[pair.Key] = pair.Value;
        var windows = discoveredWindows.Select(window =>
        {
            var matchingRemote = remoteSources.Where(s => s.Matches(window)).ToArray();
            if (matchingRemote.Length == 1)
            {
                var identity = matchingRemote[0].Identity;
                remote.StatusBarBackgrounds.TryGetValue(identity, out var remoteStatusBarBackground);
                return window with { Workspace = identity, ProjectIdentity = matchingRemote[0].OrderIdentity,
                    StatusBarBackground = remoteStatusBarBackground };
            }
            if (matchingRemote.Length > 1)
            {
                warnings.Add($"Multiple remote sources match {window.Project}.");
                return window;
            }
            var candidates = paths.Where(path => MatchesProject(window.Project, path)).ToArray();
            var liveCandidates = candidates.Where(livePaths.Contains).ToArray();
            var selected = liveCandidates.Length == 1 ? liveCandidates[0] :
                candidates.Length == 1 ? candidates[0] : null;
            if (selected is null && candidates.Length > 1)
                warnings.Add($"Ambiguous workspace for {window.Project}; status is unknown.");
            string? statusBarBackground = null;
            if (selected is not null)
            {
                try { statusBarBackground = WorkspaceColorCustomization.ReadLocal(selected); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                    FormatException)
                {
                    var warning = $"Workspace color for {window.Project}: {ex.Message}";
                    warnings.Add(warning);
                    LogOnce(warning);
                }
            }
            return window with { Workspace = selected, ProjectIdentity = selected,
                StatusBarBackground = statusBarBackground };
        }).ToList();
        return new(windows, sessions, workspaceErrors, warnings,
            AvailableWorkspaces(livePaths, remote.Available)) { RemoteSources = remoteSources.ToArray() };
    }

    internal static HashSet<string> AvailableWorkspaces(
        IEnumerable<string> local, IEnumerable<string> remote) =>
        local.Concat(remote).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool MatchesProject(string project, string path)
    {
        var name = Path.GetFileName(path);
        return project.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            project.StartsWith(name + " [", StringComparison.OrdinalIgnoreCase) ||
            project.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLive(string directory, DateTimeOffset lastEvent)
    {
        var locks = Directory.GetFiles(directory, "inuse.*.lock");
        foreach (var file in locks)
        {
            var parts = Path.GetFileName(file).Split('.');
            if (parts.Length < 3 || !int.TryParse(parts[1], out var pid)) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited && process.StartTime.ToUniversalTime() <= lastEvent.UtcDateTime)
                    return true;
            }
            catch (ArgumentException) { }
        }
        return false;
    }

    private void ReadIdePaths(HashSet<string> paths, HashSet<string> livePaths, List<string> warnings)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "ide");
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*.lock"))
        {
            try
            {
                if (new FileInfo(file).Length == 0) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("pid", out var pid)) continue;
                using var process = Process.GetProcessById(pid.GetInt32());
                if (process.HasExited) continue;
                if (doc.RootElement.TryGetProperty("workspaceFolders", out var folders))
                    foreach (var folder in folders.EnumerateArray())
                    {
                        var path = Identity.NormalizePath(folder.GetString() ?? "");
                        if (path.Length == 0) continue;
                        paths.Add(path);
                        livePaths.Add(path);
                    }
            }
            catch (ArgumentException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
            {
                var message = $"Cannot read IDE workspace metadata: {ex.Message}";
                warnings.Add(message);
                LogOnce(message);
            }
        }
    }

    internal static Dictionary<string, string> ParseMetadata(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 || char.IsWhiteSpace(line[0])) continue;
            var key = line[..separator];
            if (key is not ("cwd" or "name" or "client_name")) continue;
            var value = line[(separator + 1)..].Trim();
            if (value.StartsWith('"')) value = JsonSerializer.Deserialize<string>(value) ?? "";
            else if (value.StartsWith('\'') && value.EndsWith('\''))
                value = value[1..^1].Replace("''", "'");
            result[key] = value;
        }
        return result;
    }

    private static Dictionary<string, string> ReadMetadata(string path) => ParseMetadata(File.ReadAllText(path));

    private void LogOnce(string message)
    {
        if (loggedErrors.Add(message)) AppStorage.Log(message);
    }

}
