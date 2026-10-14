using System.IO;
using System.Text.Json;
using VSCDeck.Core;

namespace VSCDeck.App;

public sealed class RemoteSource
{
    public string Project { get; set; } = "";
    public string Workspace { get; set; } = "";
    public string DockerContext { get; set; } = "desktop-linux";
    public string? SshHost { get; set; }
    public string SessionRoot { get; set; } = "/home/vscode/.copilot/session-state";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Identity => "docker:" + (SshHost ?? DockerContext).ToLowerInvariant() + ":" + Core.Identity.NormalizePath(Workspace);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Project) || string.IsNullOrWhiteSpace(Workspace) ||
            string.IsNullOrWhiteSpace(DockerContext) || string.IsNullOrWhiteSpace(SessionRoot) || !SessionRoot.StartsWith('/') ||
            (SshHost is not null && (SshHost.Length == 0 || SshHost.StartsWith('-') ||
                SshHost.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_' or '@')))))
            throw new FormatException("Invalid remote source. Specify a project, workspace label, Docker context, absolute session root, and valid SSH host.");
    }

    public bool Matches(CodeWindow window) =>
        window.Project.StartsWith(Project + " [", StringComparison.OrdinalIgnoreCase) &&
        window.Project.Contains("@ " + (SshHost ?? DockerContext) + "]", StringComparison.OrdinalIgnoreCase);
}

internal sealed record RemoteSnapshot(List<SessionStatus> Sessions, Dictionary<string, string> Errors,
    List<string> Warnings, HashSet<string> Available, Dictionary<string, string> StatusBarBackgrounds);

internal sealed class RemoteMonitor : IDisposable
{
    private readonly List<Reader> readers;
    private readonly Dictionary<string, SshCommandChannel> channels = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    public RemoteMonitor(IEnumerable<RemoteSource> sources)
    {
        readers = sources.Select(source =>
        {
            SshCommandChannel? channel = null;
            if (source.SshHost is { } host && !channels.TryGetValue(host, out channel))
                channels[host] = channel = new SshCommandChannel(host);
            return new Reader(source, channel);
        }).ToList();
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var channel in channels.Values) channel.Dispose();
    }

    public RemoteSnapshot Scan(IReadOnlyCollection<CodeWindow> windows)
    {
        if (disposed) return new([], [], [], [], []);
        var results = readers.Select(reader => reader.Poll(windows)).ToArray();
        return new(results.SelectMany(r => r.Sessions).ToList(),
            results.SelectMany(r => r.Errors).ToDictionary(p => p.Key, p => p.Value),
            results.SelectMany(r => r.Warnings).ToList(),
            results.SelectMany(r => r.Available).ToHashSet(StringComparer.OrdinalIgnoreCase),
            results.SelectMany(r => r.StatusBarBackgrounds)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class Reader(RemoteSource source, SshCommandChannel? channel)
    {
        private readonly Dictionary<string, long> offsets = [];
        private readonly Dictionary<string, SessionTracker> trackers = [];
        private string? container;
        private DateTimeOffset rediscoverAt;
        private string lastError = "";
        private string lastColorError = "";
        private string? statusBarBackground;
        private bool active;
        private DateTimeOffset nextPoll;
        private Task<RemoteSnapshot>? poll;
        private bool discardPoll;
        private RemoteSnapshot cached = new([], [], [], [], []);

        public RemoteSnapshot Poll(IReadOnlyCollection<CodeWindow> windows)
        {
            var matchingWindow = windows.Any(source.Matches);
            if (!matchingWindow && poll is not null) discardPoll = true;
            if (poll is { IsCompleted: true })
            {
                var completed = poll;
                poll = null;
                var result = completed.GetAwaiter().GetResult();
                if (!discardPoll) cached = result;
                nextPoll = discardPoll ? DateTimeOffset.MinValue :
                    DateTimeOffset.UtcNow.AddSeconds(3);
                discardPoll = false;
            }
            if (!matchingWindow)
            {
                active = false;
                cached = new([], [], [], [], []);
                return cached;
            }
            if (!active)
            {
                active = true;
                nextPoll = DateTimeOffset.MinValue;
                rediscoverAt = DateTimeOffset.MinValue;
            }
            if (poll is null && DateTimeOffset.UtcNow >= nextPoll)
                poll = Scan();
            return cached;
        }

        public async Task<RemoteSnapshot> Scan()
        {
            try
            {
                source.Validate();
                if (container is null || DateTimeOffset.UtcNow >= rediscoverAt)
                {
                    var ids = await Command(["docker", .. ContextArgs(), "ps", "--filter",
                        "label=devcontainer.local_folder=" + source.Workspace, "--format", "{{.ID}}"]);
                    var candidates = ids.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (candidates.Length != 1)
                        throw new IOException($"Expected one running container for {source.Project}; found {candidates.Length}.");
                    if (container != candidates[0]) { offsets.Clear(); trackers.Clear(); }
                    container = candidates[0];
                    rediscoverAt = DateTimeOffset.UtcNow.AddSeconds(30);
                }
                var input = JsonSerializer.Serialize(new { root = source.SessionRoot, offsets });
                var output = await Command(["docker", .. ContextArgs(), "exec", "-i", container,
                    "python3", "-c", HelperScript], input);
                using var document = JsonDocument.Parse(output);
                var root = document.RootElement;
                if (!root.GetProperty("available").GetBoolean())
                    throw new IOException($"Copilot session directory unavailable in {source.Project}.");
                var sessions = new List<SessionStatus>();
                foreach (var item in root.GetProperty("sessions").EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString() ?? throw new JsonException("Remote session ID missing.");
                    var metadata = CopilotMonitor.ParseMetadata(item.GetProperty("metadata").GetString() ?? "");
                    var client = metadata.GetValueOrDefault("client_name", "");
                    if (client.Length > 0 && !client.Contains("vscode", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!trackers.TryGetValue(id, out var tracker) || item.GetProperty("reset").GetBoolean())
                        trackers[id] = tracker = new SessionTracker(id);
                    tracker.SetWorkspace(metadata.GetValueOrDefault("cwd", ""));
                    if (metadata.TryGetValue("name", out var name))
                        tracker.Title = SessionTracker.Shorten(name, 100);
                    foreach (var entry in item.GetProperty("events").EnumerateArray())
                        tracker.Apply(entry);
                    offsets[id] = item.GetProperty("offset").GetInt64();
                    if (tracker.Ended || (tracker.LastEvent - DateTimeOffset.UnixEpoch).TotalSeconds <
                        item.GetProperty("started").GetDouble())
                        continue;
                    var workspaceName = Path.GetFileName(tracker.Workspace);
                    if (!workspaceName.Equals(source.Project, StringComparison.OrdinalIgnoreCase)) continue;
                    sessions.Add(tracker.Status with { Workspace = source.Identity });
                }
                var colorWarnings = await ReadWorkspaceColor();
                lastError = "";
                return new(sessions, [], colorWarnings, [source.Identity], Colors());
            }
            catch (OperationCanceledException) when (channel?.IsDisposed == true)
            {
                return new([], [], [], [], Colors());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                System.ComponentModel.Win32Exception or FormatException or OperationCanceledException or InvalidOperationException)
            {
                rediscoverAt = DateTimeOffset.MinValue;
                if (ex is JsonException or FormatException or InvalidOperationException)
                {
                    offsets.Clear();
                    trackers.Clear();
                }
                var error = $"Remote status for {source.Project}: {ex.Message}";
                if (lastError != error) AppStorage.Log(error);
                lastError = error;
                return new([], new() { [source.Identity] = error }, [error], [], Colors());
            }
        }

        private async Task<List<string>> ReadWorkspaceColor()
        {
            try
            {
                string settings;
                if (source.SshHost is null)
                {
                    var path = WorkspaceColorCustomization.SettingsPath(source.Workspace);
                    settings = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
                }
                else
                {
                    settings = await channel!.Run(["python3", "-c", WorkspaceSettingsScript,
                        source.Workspace]);
                }
                statusBarBackground = WorkspaceColorCustomization.Parse(settings);
                lastColorError = "";
                return [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                FormatException or System.ComponentModel.Win32Exception or InvalidOperationException or
                OperationCanceledException)
            {
                statusBarBackground = null;
                var error = $"Workspace color for {source.Project}: {ex.Message}";
                if (lastColorError != error) AppStorage.Log(error);
                lastColorError = error;
                return [error];
            }
        }

        private Dictionary<string, string> Colors() => statusBarBackground is null
            ? []
            : new(StringComparer.OrdinalIgnoreCase) { [source.Identity] = statusBarBackground };

        private string[] ContextArgs() => source.SshHost is null ? ["--context", source.DockerContext] : [];

        private async Task<string> Command(string[] command, string? input = null)
        {
            if (channel is not null) return await channel.Run(command, input);
            return await ProcessCommand.Run(command, input, TimeSpan.FromSeconds(60), "Docker/SSH status command");
        }

    }

    private const string HelperScript = """
import glob, json, os, sys
request = json.load(sys.stdin)
root = request["root"]
result = {"available": os.path.isdir(root), "sessions": []}
types = {"session.start", "user.message", "assistant.turn_start", "assistant.message",
         "assistant.turn_end", "tool.execution_start", "tool.execution_complete",
         "permission.requested", "permission.completed", "session.idle",
         "session.task_complete", "session.shutdown", "session.error", "session.abort", "abort"}
if result["available"]:
    boot = next(int(x.split()[1]) for x in open("/proc/stat") if x.startswith("btime "))
    ticks = os.sysconf("SC_CLK_TCK")
    for directory in glob.glob(os.path.join(root, "*")):
        metadata = os.path.join(directory, "workspace.yaml")
        events = os.path.join(directory, "events.jsonl")
        if not os.path.isfile(metadata) or not os.path.isfile(events):
            continue
        starts = []
        for lock in glob.glob(os.path.join(directory, "inuse.*.lock")):
            pid = os.path.basename(lock).split(".")[1]
            if not pid.isdigit():
                continue
            try:
                stat = open("/proc/" + pid + "/stat").read().rsplit(")", 1)[1].split()
                if stat[0] != "Z":
                    starts.append(boot + int(stat[19]) / ticks)
            except FileNotFoundError:
                continue
        if not starts:
            continue
        ident = os.path.basename(directory)
        old = request["offsets"].get(ident, 0)
        with open(events, "rb") as stream:
            size = os.fstat(stream.fileno()).st_size
            reset = size < old
            start = 0 if reset else old
            stream.seek(start)
            projected = []
            offset = start
            while True:
                line = stream.readline()
                if not line or not line.endswith(b"\n"):
                    break
                event = json.loads(line)
                offset = stream.tell()
                if event.get("type") not in types:
                    continue
                data = event.get("data", {})
                selected = {k: data[k] for k in
                    ("toolCallId", "toolName", "toolTitle", "requestId", "context") if k in data}
                if "content" in data:
                    selected["content"] = str(data["content"])[:500]
                if isinstance(data.get("arguments"), dict):
                    selected["arguments"] = {"question": str(data["arguments"].get("question", ""))[:500]}
                projected.append({"type": event["type"], "timestamp": event.get("timestamp"), "data": selected})
        result["sessions"].append({
            "id": ident, "metadata": open(metadata, encoding="utf-8").read(),
            "events": projected, "offset": offset,
            "reset": reset, "started": min(starts)
        })
json.dump(result, sys.stdout, ensure_ascii=False)
""";

    private const string WorkspaceSettingsScript = """
import os, sys
workspace = sys.argv[1]
path = workspace if workspace.lower().endswith(".code-workspace") else os.path.join(workspace, ".vscode", "settings.json")
if os.path.isfile(path):
    with open(path, encoding="utf-8") as stream:
        sys.stdout.write(stream.read())
""";
}
