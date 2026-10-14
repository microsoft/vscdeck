using System.Text.Json;

namespace VSDeck.Core;

public enum Activity { Unknown, Idle, Running, Question }

public sealed record SessionStatus(string Id, string Workspace, string Title,
    Activity Activity, string Description, DateTimeOffset LastEvent);

public sealed record WindowStatus(Activity Activity, IReadOnlyList<SessionStatus> Sessions, string Detail)
{
    public static WindowStatus Aggregate(IEnumerable<SessionStatus> source, string? error = null, bool sourceAvailable = false)
    {
        var sessions = source.OrderByDescending(s => s.Activity == Activity.Question)
            .ThenByDescending(s => s.LastEvent).ToArray();
        var activity = error is not null ? Activity.Unknown :
            sessions.Any(s => s.Activity == Activity.Question) ? Activity.Question :
            sessions.Any(s => s.Activity == Activity.Running) ? Activity.Running :
            sessions.Length > 0 || sourceAvailable ? Activity.Idle : Activity.Unknown;
        if (sessions.Any(s => s.Activity == Activity.Question))
            activity = Activity.Question;
        var detail = error ?? (sessions.Length == 0
            ? sourceAvailable ? "No live Copilot sessions in this workspace." :
                "No readable Copilot agent-host session is associated with this workspace."
            : string.Join("\n\n", sessions.Select(s =>
                $"{s.Title}\n{s.Activity}: {s.Description}\nSession: {s.Id}")));
        return new(activity, sessions, detail);
    }
}

public sealed class SessionTracker(string id)
{
    private readonly Dictionary<string, string> questions = [];
    private readonly HashSet<string> tools = [];
    private bool running;
    public string Workspace { get; private set; } = "";
    public string Client { get; set; } = "";
    public string Title { get; set; } = "Copilot session";
    public string Description { get; private set; } = "Idle";
    public DateTimeOffset LastEvent { get; private set; }
    public bool Ended { get; private set; }

    public SessionStatus Status => new(id, Workspace, Title,
        questions.Count > 0 ? Activity.Question : running || tools.Count > 0
            ? Activity.Running : Activity.Idle,
        questions.Count > 0 ? string.Join("\n", questions.Values) : Description, LastEvent);

    public void SetWorkspace(string path) => Workspace = Identity.NormalizePath(path);

    public void Apply(JsonElement item)
    {
        if (!item.TryGetProperty("type", out var typeValue) ||
            !item.TryGetProperty("data", out var data)) return;
        var type = typeValue.GetString();
        if (item.TryGetProperty("timestamp", out var timestamp) &&
            timestamp.TryGetDateTimeOffset(out var time)) LastEvent = time;
        var callId = Text(data, "toolCallId");
        switch (type)
        {
            case "session.start":
                if (data.TryGetProperty("context", out var context))
                    SetWorkspace(Text(context, "cwd"));
                break;
            case "user.message":
                Ended = false;
                running = true;
                Description = Shorten(Text(data, "content"), 240);
                if (Title == "Copilot session") Title = Shorten(Text(data, "content"), 100);
                break;
            case "assistant.turn_start":
                running = true;
                Ended = false;
                break;
            case "assistant.message":
                var content = Text(data, "content");
                if (!string.IsNullOrWhiteSpace(content)) Description = Shorten(content, 240);
                break;
            case "tool.execution_start":
                tools.Add(callId);
                var toolName = Text(data, "toolName");
                var toolTitle = Text(data, "toolTitle");
                if (!string.IsNullOrEmpty(toolTitle)) Description = Shorten(toolTitle, 240);
                if (toolName.EndsWith("ask_user", StringComparison.OrdinalIgnoreCase) ||
                    toolName.EndsWith("askUserQuestion", StringComparison.OrdinalIgnoreCase))
                {
                    var question = data.TryGetProperty("arguments", out var arguments)
                        ? Text(arguments, "question") : "";
                    questions["tool:" + callId] = string.IsNullOrWhiteSpace(question)
                        ? "Copilot is asking a question." : Shorten(question, 500);
                }
                break;
            case "tool.execution_complete":
                tools.Remove(callId);
                questions.Remove("tool:" + callId);
                break;
            case "permission.requested":
                questions["permission:" + Text(data, "requestId")] = "Copilot needs permission to continue.";
                break;
            case "permission.completed":
                questions.Remove("permission:" + Text(data, "requestId"));
                break;
            case "assistant.turn_end":
                running = false;
                if (tools.Count == 0 && questions.Count == 0) Description = "Idle";
                break;
            case "session.idle":
            case "session.task_complete":
            case "session.shutdown":
            case "session.error":
            case "session.abort":
                running = false;
                tools.Clear();
                questions.Clear();
                Description = type == "session.error" ? "Stopped with an agent error" : "Idle";
                Ended = type == "session.shutdown";
                break;
        }
    }

    public static string Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) &&
        child.ValueKind == JsonValueKind.String ? child.GetString() ?? "" : "";

    public static string Shorten(string text, int length)
    {
        var compact = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= length ? compact : compact[..(length - 3)] + "...";
    }
}

public static class Identity
{
    public static string NormalizePath(string path) =>
        path.Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    public static string ProjectFromTitle(string title)
    {
        var parts = title.Replace(" \u2014 ", " - ").Replace(" \u2013 ", " - ")
            .Split(" - ", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "VS Code";
        var index = parts.Length - 2;
        if (parts[^1].Equals("Insiders", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
            index--;
        return parts[Math.Max(0, index)].Trim().TrimStart('\u25cf', '*').Trim();
    }

    public static List<T> Sort<T>(IEnumerable<T> items, IReadOnlyList<string> saved, Func<T, string> key) =>
        items.OrderBy(item =>
        {
            var index = saved.ToList().FindIndex(id => id.Equals(key(item), StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }).ToList();

    public static List<string> RememberOrder(IEnumerable<string> liveOrder, IEnumerable<string> saved) =>
        liveOrder.Concat(saved).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
