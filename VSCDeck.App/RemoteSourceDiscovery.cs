using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VSCDeck.App;

internal sealed record RemoteDiscoveryResult(IReadOnlyList<RemoteSource> Sources, IReadOnlyList<string> Warnings);

internal static partial class RemoteSourceDiscovery
{
    public static async Task<RemoteDiscoveryResult> Discover()
    {
        var windows = NativeWindows.Discover();
        var hosts = windows.Select(WindowHost).Where(host => host is not null)
            .Select(host => host!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (hosts.Count == 0) return new([], []);

        var sources = new List<RemoteSource>();
        var warnings = new List<string>();
        var contexts = await DockerContexts(warnings);
        foreach (var context in contexts.Where(context => hosts.Contains(context)))
            await DiscoverEndpoint(context, null, windows,
                arguments => ProcessCommand.Run(["docker", "--context", context, .. arguments],
                    null, TimeSpan.FromSeconds(20), $"Docker context {context} discovery"), sources, warnings);

        var sshHosts = ReadSshHosts(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config"));
        foreach (var host in hosts.Where(sshHosts.Contains))
        {
            using var channel = new SshCommandChannel(host);
            await DiscoverEndpoint("default", host, windows, arguments => channel.Run(["docker", .. arguments]),
                sources, warnings);
        }

        var distinct = sources.GroupBy(source => source.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).OrderBy(source => source.Project, StringComparer.OrdinalIgnoreCase).ToList();
        return new(distinct, warnings);
    }

    internal static HashSet<string> ReadSshHosts(string path)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return hosts;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Host ", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var host in trimmed[5..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (host.IndexOfAny(['*', '?', '!']) < 0)
                    hosts.Add(host);
        }
        return hosts;
    }

    internal static List<RemoteSource> ParseContainers(
        string json, string dockerContext, string? sshHost, IReadOnlyCollection<CodeWindow> windows)
    {
        using var document = JsonDocument.Parse(json);
        var sources = new List<RemoteSource>();
        foreach (var container in document.RootElement.EnumerateArray())
        {
            if (!container.TryGetProperty("Config", out var config) ||
                !config.TryGetProperty("Labels", out var labels) ||
                labels.ValueKind != JsonValueKind.Object ||
                !labels.TryGetProperty("devcontainer.local_folder", out var folder))
                continue;
            var workspace = folder.GetString() ?? "";
            var project = workspace.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() ?? "";
            if (project.Length == 0) continue;
            var source = new RemoteSource
            {
                Project = project,
                Workspace = workspace,
                DockerContext = dockerContext,
                SshHost = sshHost,
                SessionRoot = SessionRoot(config, labels)
            };
            if (windows.Any(source.Matches)) sources.Add(source);
        }
        return sources;
    }

    private static async Task<List<string>> DockerContexts(List<string> warnings)
    {
        try
        {
            var output = await ProcessCommand.Run(["docker", "context", "ls", "--format", "{{json .}}"],
                null, TimeSpan.FromSeconds(15), "Docker context discovery");
            var contexts = new List<string>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("Name", out var name) && name.GetString() is { Length: > 0 } value &&
                    (!root.TryGetProperty("Error", out var error) || string.IsNullOrWhiteSpace(error.GetString())))
                    contexts.Add(value);
            }
            return contexts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or Win32Exception)
        {
            warnings.Add($"Docker context discovery: {ex.Message}");
            return [];
        }
    }

    private static async Task DiscoverEndpoint(string dockerContext, string? sshHost,
        IReadOnlyCollection<CodeWindow> windows, Func<string[], Task<string>> command,
        List<RemoteSource> sources, List<string> warnings)
    {
        var display = sshHost is null ? $"Docker context {dockerContext}" : $"SSH host {sshHost}";
        try
        {
            var ids = (await command(["ps", "-q", "--filter", "label=devcontainer.local_folder"]))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0) return;
            var json = await command(["inspect", .. ids]);
            sources.AddRange(ParseContainers(json, dockerContext, sshHost, windows));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            Win32Exception or FormatException or OperationCanceledException or InvalidOperationException)
        {
            var warning = $"{display} discovery: {ex.Message}";
            warnings.Add(warning);
            AppStorage.Log(warning);
        }
    }

    private static string SessionRoot(JsonElement config, JsonElement labels)
    {
        var user = "";
        if (labels.TryGetProperty("devcontainer.metadata", out var metadata) &&
            metadata.GetString() is { Length: > 0 } text)
        {
            using var document = JsonDocument.Parse(text);
            foreach (var item in document.RootElement.EnumerateArray())
                if (item.TryGetProperty("remoteUser", out var remoteUser) &&
                    remoteUser.GetString() is { Length: > 0 } value)
                    user = value;
        }
        if (user.Length == 0 && config.TryGetProperty("User", out var configuredUser))
            user = configuredUser.GetString()?.Split(':')[0] ?? "";
        if (user.Length == 0) user = "vscode";
        return user == "root" ? "/root/.copilot/session-state" : $"/home/{user}/.copilot/session-state";
    }

    private static string? WindowHost(CodeWindow window)
    {
        var match = DevContainerHost().Match(window.Project);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"\[Dev Container: .+ @ ([^\]]+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex DevContainerHost();
}
