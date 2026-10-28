using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using VSCDeck.Core;

namespace VSCDeck.App;

internal sealed class ProjectConfigurationCatalog
{
    internal sealed record Location(ProjectConfiguration Configuration, string WindowProject, string? Host, bool Container);
    private sealed record Entry(Location Location, bool Open);
    private readonly Dictionary<string, List<Entry>> entries = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset nextRead;

    internal List<CodeWindow> Attach(IReadOnlyList<CodeWindow> windows, List<string> warnings,
        IReadOnlyList<RemoteSource>? sources = null)
    {
        var executables = new Dictionary<int, string>();
        foreach (var pid in windows.Select(window => window.ProcessId).Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                executables[pid] = process.MainModule?.FileName ??
                    throw new IOException("Cannot identify the VS Code executable.");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or IOException)
            {
                warnings.Add("Recent projects: " + ex.Message);
            }
        }
        if (DateTimeOffset.UtcNow >= nextRead || executables.Values.Any(executable => !entries.ContainsKey(executable)))
        {
            entries.Clear();
            foreach (var executable in executables.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                entries[executable] = Read(executable, warnings);
            nextRead = DateTimeOffset.UtcNow.AddSeconds(10);
        }
        return windows.Select(window =>
        {
            if (!executables.TryGetValue(window.ProcessId, out var executable)) return window;
            var candidates = entries[executable].Where(entry => Matches(entry.Location, window)).ToArray();
            var open = candidates.Where(entry => entry.Open).ToArray();
            if (open.Length > 0) candidates = open;
            var distinct = candidates.Select(entry => entry.Location.Configuration)
                .DistinctBy(configuration => configuration.Id).ToArray();
            if (distinct.Length == 1)
                return window with
                {
                    Configuration = ApplyProjectIdentity(distinct[0], window, sources ?? []) with
                    {
                        ButtonOrderIdentity = window.OrderIdentity
                    }
                };
            warnings.Add($"Recent projects: {(distinct.Length == 0 ? "no exact reopen target" : "ambiguous reopen targets")} for {window.Project}.");
            return window;
        }).ToList();
    }

    private static List<Entry> Read(string executable, List<string> warnings)
    {
        var product = Path.GetFileNameWithoutExtension(executable).Contains("Insiders", StringComparison.OrdinalIgnoreCase)
            ? "Code - Insiders" : "Code";
        var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), product, "User");
        var result = new List<Entry>();
        var statePath = Path.Combine(user, "globalStorage", "storage.json");
        if (File.Exists(statePath))
            ReadFile(statePath, root =>
            {
                if (!root.TryGetProperty("windowsState", out var state)) return;
                if (state.TryGetProperty("lastActiveWindow", out var active)) AddWindow(active);
                if (state.TryGetProperty("openedWindows", out var opened))
                    foreach (var window in opened.EnumerateArray()) AddWindow(window);
                void AddWindow(JsonElement window)
                {
                    if (window.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.String)
                        Add(folder.GetString()!, false, true);
                    if (window.TryGetProperty("workspace", out var workspace) && workspace.ValueKind == JsonValueKind.Object &&
                        workspace.TryGetProperty("configPath", out var config) && config.ValueKind == JsonValueKind.String)
                        Add(config.GetString()!, true, true);
                }
            });
        var storage = Path.Combine(user, "workspaceStorage");
        if (Directory.Exists(storage))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(storage))
                {
                    var path = Path.Combine(directory, "workspace.json");
                    if (File.Exists(path)) ReadFile(path, root =>
                    {
                        if (root.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.String)
                            Add(folder.GetString()!, false, false);
                        if (root.TryGetProperty("workspace", out var workspace) && workspace.ValueKind == JsonValueKind.String)
                            Add(workspace.GetString()!, true, false);
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add("Recent project metadata: " + ex.Message);
            }
        }
        return result;

        void Add(string uri, bool workspace, bool open)
        {
            if (!open && uri.StartsWith("vscode-remote://", StringComparison.OrdinalIgnoreCase))
            {
                var separator = uri.IndexOf('/', "vscode-remote://".Length);
                var authority = Uri.UnescapeDataString(separator < 0 ? uri["vscode-remote://".Length..] :
                    uri["vscode-remote://".Length..separator]);
                // Unrelated historical providers are not reopen candidates for supported windows.
                if (!authority.StartsWith("ssh-remote+", StringComparison.Ordinal) &&
                    !authority.StartsWith("dev-container+", StringComparison.Ordinal)) return;
            }
            try { result.Add(new(ParseLocation(uri, workspace, executable), open)); }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or InvalidOperationException)
            {
                warnings.Add("Recent project metadata: " + ex.Message);
            }
        }
        void ReadFile(string path, Action<JsonElement> read)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var json = JsonDocument.Parse(stream);
                read(json.RootElement);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                warnings.Add($"Recent project metadata ({path}): {ex.Message}");
            }
        }
    }

    internal static Location ParseLocation(string uri, bool workspace, string executable)
    {
        string path;
        string projectIdentity;
        string? host = null;
        var container = false;
        var environment = "Local";
        if (uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            path = new Uri(uri).LocalPath;
            projectIdentity = Core.Identity.NormalizePath(path);
        }
        else if (uri.StartsWith("vscode-remote://", StringComparison.OrdinalIgnoreCase))
        {
            var split = uri.IndexOf('/', "vscode-remote://".Length);
            if (split < 0) throw new FormatException("Remote workspace URI has no path.");
            var authority = Uri.UnescapeDataString(uri["vscode-remote://".Length..split]);
            path = Uri.UnescapeDataString(uri[split..]);
            if (authority.StartsWith("ssh-remote+", StringComparison.Ordinal))
            {
                host = authority["ssh-remote+".Length..];
                environment = "SSH: " + host;
                projectIdentity = "ssh:" + host.ToLowerInvariant() + ":" + Core.Identity.NormalizePath(path);
            }
            else if (authority.StartsWith("dev-container+", StringComparison.Ordinal))
            {
                container = true;
                var separator = authority.IndexOf('@');
                var hex = authority["dev-container+".Length..(separator < 0 ? authority.Length : separator)];
                using var metadata = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromHexString(hex)));
                var root = metadata.RootElement;
                if (!root.TryGetProperty("hostPath", out var hostPathElement) ||
                    hostPathElement.ValueKind != JsonValueKind.String ||
                    hostPathElement.GetString() is not { Length: > 0 } hostPath)
                    throw new FormatException("Dev Container metadata has no host path.");
                if (separator >= 0)
                {
                    var remote = authority[(separator + 1)..];
                    if (!remote.StartsWith("ssh-remote+", StringComparison.Ordinal))
                        throw new FormatException("Unsupported Dev Container remote authority.");
                    host = remote["ssh-remote+".Length..];
                }
                else if (root.TryGetProperty("settings", out var settings) &&
                    settings.TryGetProperty("context", out var context))
                    host = context.GetString();
                environment = "Dev Container: " + (host ?? "local Docker");
                projectIdentity = separator >= 0
                    ? "ssh:" + host!.ToLowerInvariant() + ":" + Core.Identity.NormalizePath(hostPath)
                    : Core.Identity.NormalizePath(hostPath);
            }
            else throw new FormatException("Unsupported remote workspace authority.");
        }
        else throw new FormatException("Unsupported workspace URI.");
        var name = path.TrimEnd('/', '\\').Split('/', '\\').Last();
        if (workspace) name = Path.GetFileNameWithoutExtension(name);
        var configuration = new ProjectConfiguration(projectIdentity, name, uri, executable, workspace, environment);
        configuration.Validate();
        return new(configuration, name, host, container);
    }

    internal static bool Matches(Location location, CodeWindow window)
    {
        var name = location.WindowProject;
        if (!window.Project.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            !window.Project.StartsWith(name + " [", StringComparison.OrdinalIgnoreCase)) return false;
        if (location.Container)
            return window.Project.Contains("[Dev Container:", StringComparison.OrdinalIgnoreCase) &&
                (location.Host is null || window.Project.Contains("@ " + location.Host + "]", StringComparison.OrdinalIgnoreCase));
        if (location.Host is not null)
            return window.Project.EndsWith("[SSH: " + location.Host + "]", StringComparison.OrdinalIgnoreCase);
        return !window.Project.Contains(" [", StringComparison.Ordinal);
    }

    internal static ProcessStartInfo CreateLaunch(ProjectConfiguration configuration)
    {
        configuration.Validate();
        var start = new ProcessStartInfo(configuration.Executable) { UseShellExecute = false };
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add(configuration.IsWorkspace ? "--file-uri" : "--folder-uri");
        start.ArgumentList.Add(configuration.WorkspaceUri);
        return start;
    }

    internal static ProjectConfiguration ApplyProjectIdentity(ProjectConfiguration configuration, CodeWindow window,
        IReadOnlyList<RemoteSource> sources)
    {
        var mappings = sources.Where(source => source.ProjectIdentity is not null && source.Matches(window))
            .Select(source => source.OrderIdentity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return mappings.Length == 1 ? configuration with { ProjectIdentity = mappings[0] } : configuration;
    }

    internal static nint? FindOpenConfiguration(ProjectConfiguration configuration, IReadOnlyList<CodeWindow> windows)
    {
        var existing = windows.FirstOrDefault(window => window.Configuration?.Id == configuration.Id);
        if (existing is not null) return existing.Handle;
        var location = ParseLocation(configuration.WorkspaceUri, configuration.IsWorkspace, configuration.Executable);
        if (windows.Any(window => window.Configuration is null && Matches(location, window)))
            throw new InvalidOperationException("An open window has an unresolved configuration. Wait for discovery before trying again.");
        return null;
    }
}
