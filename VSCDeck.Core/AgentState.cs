using System.Text.Json;

namespace VSCDeck.Core;

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
            case "abort":
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

    public static List<T> Sort<T>(IReadOnlyList<T> items, IReadOnlyList<RelativeOrder> relations,
        Func<T, string> key)
    {
        var groups = items.Select((item, index) => new { item, index, Key = key(item) })
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var byKey = groups.ToDictionary(group => group.Key, StringComparer.OrdinalIgnoreCase);
        var known = relations.SelectMany(relation => new[] { relation.Before, relation.After })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outgoing = groups.ToDictionary(group => group.Key, _ => new HashSet<string>(
            StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var incoming = groups.ToDictionary(group => group.Key, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var relation in relations)
        {
            if (!outgoing.TryGetValue(relation.Before, out var after) ||
                !incoming.ContainsKey(relation.After) || !after.Add(relation.After))
                continue;
            incoming[relation.After]++;
        }
        var ready = new PriorityQueue<string, int>(incoming.Where(pair => pair.Value == 0)
            .Select(pair => (pair.Key, SortPriority(pair.Key))));
        var sorted = new List<T>();
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (processed.Count < groups.Length)
        {
            if (!ready.TryDequeue(out var current, out _))
                current = groups.Where(group => !processed.Contains(group.Key))
                    .OrderBy(group => SortPriority(group.Key)).First().Key;
            Process(current);
        }
        return sorted;

        int SortPriority(string identity) =>
            (known.Contains(identity) ? 0 : 1_000_000) + byKey[identity].First().index;

        void Process(string current)
        {
            if (!processed.Add(current)) return;
            sorted.AddRange(byKey[current].Select(entry => entry.item));
            foreach (var next in outgoing[current])
                if (--incoming[next] == 0) ready.Enqueue(next, SortPriority(next));
        }
    }

    public static List<RelativeOrder> RelationsFor(IEnumerable<string> order)
    {
        var identities = order.Where(identity => identity.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return identities.SelectMany((before, index) => identities.Skip(index + 1)
                .Where(after => !before.Equals(after, StringComparison.OrdinalIgnoreCase))
                .Select(after => new RelativeOrder(before, after)))
            .Distinct(RelativeOrder.Comparer).ToList();
    }

    public static List<RelativeOrder> UpdateRelations(IReadOnlyList<RelativeOrder> existing,
        IEnumerable<string> visibleOrder)
    {
        var visible = visibleOrder.Where(identity => identity.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var known = existing.SelectMany(relation => new[] { relation.Before, relation.After })
            .Concat(visible).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fullOrder = Sort(known, existing, identity => identity);
        var visibleSlots = fullOrder.Select((identity, index) => (identity, index))
            .Where(entry => visible.Contains(entry.identity, StringComparer.OrdinalIgnoreCase))
            .Select(entry => entry.index).ToArray();

        for (var index = 0; index < visibleSlots.Length; index++)
            fullOrder[visibleSlots[index]] = visible[index];
        return RelationsFor(fullOrder);
    }
}

public sealed record RelativeOrder(string Before, string After)
{
    public static IEqualityComparer<RelativeOrder> Comparer { get; } = new RelativeOrderComparer();

    private sealed class RelativeOrderComparer : IEqualityComparer<RelativeOrder>
    {
        public bool Equals(RelativeOrder? left, RelativeOrder? right) =>
            left is not null && right is not null &&
            left.Before.Equals(right.Before, StringComparison.OrdinalIgnoreCase) &&
            left.After.Equals(right.After, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(RelativeOrder value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Before),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.After));
    }
}
