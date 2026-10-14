using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VSCDeck.Core;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    count++;
    Console.WriteLine("PASS: " + name);
}
string Event(string type, object data) => JsonSerializer.Serialize(new { type, data, timestamp = DateTimeOffset.UtcNow });
void Apply(SessionTracker tracker, string type, object data)
{
    using var json = JsonDocument.Parse(Event(type, data));
    tracker.Apply(json.RootElement);
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (FormatException) { Check(true, name); return; }
    throw new InvalidOperationException("FAIL: " + name);
}

var tracker = new SessionTracker("a") { Title = "Build overview" };
Apply(tracker, "session.start", new { context = new { cwd = @"C:\Project\" } });
Check(tracker.Workspace == @"c:\project", "Workspace identity is normalized");
Check(tracker.Status.Activity == Activity.Idle, "New session is idle");
Apply(tracker, "user.message", new { content = "Build my utility" });
Check(tracker.Status.Activity == Activity.Running, "User request starts work");
Apply(tracker, "tool.execution_start", new { toolCallId = "q1", toolName = "ask_user", arguments = new { question = "Choose a size?" } });
Check(tracker.Status.Activity == Activity.Question && tracker.Status.Description == "Choose a size?", "Question text and state");
Apply(tracker, "assistant.turn_end", new { });
Check(tracker.Status.Activity == Activity.Question, "Turn boundary does not hide a pending question");
Apply(tracker, "tool.execution_start", new { toolCallId = "q2", toolName = "functions.ask_user", arguments = new { question = "Choose a hotkey?" } });
Apply(tracker, "tool.execution_complete", new { toolCallId = "q1" });
Check(tracker.Status.Activity == Activity.Question && tracker.Status.Description.Contains("hotkey"), "Multiple questions tracked independently");
Apply(tracker, "tool.execution_complete", new { toolCallId = "q2" });
Apply(tracker, "assistant.turn_start", new { });
Check(tracker.Status.Activity == Activity.Running, "Answer resumes running");
Apply(tracker, "permission.requested", new { requestId = "p1" });
Check(tracker.Status.Activity == Activity.Question, "Permission prompt is a question");
Apply(tracker, "permission.completed", new { requestId = "p1" });
Check(tracker.Status.Activity == Activity.Running, "Permission completion clears only that prompt");
Apply(tracker, "tool.execution_start", new { toolCallId = "build", toolName = "powershell", toolTitle = "Building application" });
Apply(tracker, "assistant.turn_end", new { });
Check(tracker.Status.Activity == Activity.Running, "Outstanding tool stays running after turn ends");
Apply(tracker, "tool.execution_complete", new { toolCallId = "build" });
Apply(tracker, "session.task_complete", new { });
Check(tracker.Status.Activity == Activity.Idle && tracker.Status.Description == "Idle", "Task completion returns to idle");
Apply(tracker, "user.message", new { content = "Another task" });
Check(tracker.Status.Activity == Activity.Running, "Completed session can be reused");

Apply(tracker, "tool.execution_start", new { toolCallId = "cancelled-question",
    toolName = "ask_user", arguments = new { question = "Continue?" } });
Apply(tracker, "permission.requested", new { requestId = "cancelled-permission" });
Apply(tracker, "abort", new { });
Apply(tracker, "assistant.turn_end", new { });
Check(tracker.Status.Activity == Activity.Idle && tracker.Status.Description == "Idle",
    "Closing a chat clears outstanding questions, permissions, and tools on abort");
Apply(tracker, "user.message", new { content = "Resume after closing" });
Apply(tracker, "tool.execution_start", new { toolCallId = "cancelled-tool", toolName = "powershell" });
Apply(tracker, "abort", new { });
Apply(tracker, "assistant.turn_end", new { });
Check(tracker.Status.Activity == Activity.Idle && !tracker.Ended,
    "Closing a running chat clears work without permanently ending the session");
Apply(tracker, "user.message", new { content = "Reopen session" });
Check(tracker.Status.Activity == Activity.Running,
    "An aborted session can resume without stale question state");

var idle = new SessionStatus("idle", @"c:\project", "Idle session", Activity.Idle, "Idle", DateTimeOffset.Now);
var run = idle with { Id = "run", Activity = Activity.Running, Description = "Build" };
var question = idle with { Id = "question", Activity = Activity.Question, Description = "Approve?" };
Check(WindowStatus.Aggregate([]).Activity == Activity.Unknown, "No telemetry is unknown, not idle");
Check(WindowStatus.Aggregate([], sourceAvailable: true).Activity == Activity.Idle, "Verified source with no live sessions is idle");
Check(WindowStatus.Aggregate([idle]).Activity == Activity.Idle, "Known idle is green");
Check(WindowStatus.Aggregate([idle, run]).Activity == Activity.Running, "Any running session wins over idle");
var mixed = WindowStatus.Aggregate([idle, run, question]);
Check(mixed.Activity == Activity.Question && mixed.Sessions.Count == 3 && mixed.Detail.Contains("Approve?"), "Question wins and all sessions remain visible");
Check(WindowStatus.Aggregate([run], "Read error").Activity == Activity.Unknown, "Read failure never reports healthy running or idle");
Check(WindowStatus.Aggregate([question], "Read error").Activity == Activity.Question, "Known pending question stays visible despite another read failure");

Check(Identity.ProjectFromTitle("file.cs - Project - Visual Studio Code") == "Project", "Workspace extracted from file title");
Check(Identity.ProjectFromTitle("Project - Visual Studio Code") == "Project", "Workspace extracted from simple title");
Check(Identity.ProjectFromTitle("Project - Visual Studio Code - Insiders") == "Project", "Insiders title parsing");
Check(Identity.ProjectFromTitle("Visual Studio Code") == "VS Code", "Empty VS Code window identity");
Check(Identity.Sort(new[] { "c", "a", "b", "d" }, new[] { "b", "a" }, s => s).SequenceEqual(["b", "a", "c", "d"]), "Saved order restores; new windows retain discovery order");
Check(Identity.RememberOrder(["b", "a"], ["a", "closed", "b"]).SequenceEqual(["b", "a", "closed"]), "Reordered and closed identities persist");
Check(Identity.Sort(new[] { "B", "a" }, ["a", "b"], s => s).SequenceEqual(["a", "B"]), "Windows identity matching ignores case");

Check(Hotkey.Parse("Ctrl+Shift+1") is { Modifiers: 6, Key: 49 }, "Default Ctrl+Shift hotkey registration values");
Check(Hotkey.Parse("Win+Shift+F24") is { Modifiers: 12, Key: 135 }, "Function-key and modifier mapping");
Check(Hotkey.ParseList("Ctrl+Shift+1, Ctrl+Shift+0").Count == 2, "Per-position hotkeys");
Reject(() => Hotkey.ParseList("Ctrl+Shift+1, Shift+Ctrl+1"), "Duplicate hotkeys rejected");
Reject(() => Hotkey.Parse("Ctrl+F12"), "Reserved F12 rejected");
Reject(() => Hotkey.Parse("1"), "Unmodified key rejected");
Reject(() => Hotkey.Parse("Ctrl+F25"), "Unsupported function key rejected");
Reject(() => Hotkey.ParseList(""), "Empty hotkey list rejected");

var file = Path.Combine(Path.GetTempPath(), "vscdeck-test-" + Guid.NewGuid() + ".jsonl");
try
{
    var reader = new SessionEventReader("stream");
    File.WriteAllText(file, Event("user.message", new { content = "Start" }) + "\n", new UTF8Encoding(false));
    reader.Read(file);
    Check(reader.Tracker.Status.Activity == Activity.Running, "Initial event-file read");
    var questionLine = Event("tool.execution_start", new { toolCallId = "q", toolName = "ask_user", arguments = new { question = "Continue?" } });
    File.AppendAllText(file, questionLine[..20]);
    reader.Read(file);
    Check(reader.Tracker.Status.Activity == Activity.Running, "Incomplete final line is buffered without false error");
    File.AppendAllText(file, questionLine[20..] + "\n");
    reader.Read(file);
    Check(reader.Tracker.Status.Activity == Activity.Question, "Appended question becomes visible");
    reader.Read(file);
    Check(reader.Tracker.Status.Activity == Activity.Question, "Unchanged file is not replayed");
    File.WriteAllText(file, Event("session.idle", new { }) + "\n");
    reader.Read(file);
    Check(reader.Tracker.Status.Activity == Activity.Idle, "Truncated log resets tracker");
    File.AppendAllText(file, "{broken}\n");
    var errors = 0;
    for (var i = 0; i < 2; i++)
        try { reader.Read(file); }
        catch (JsonException) { errors++; }
    Check(errors == 2, "Malformed log stays an explicit failure on subsequent polls");
    File.WriteAllText(file, Event("session.idle", new { }) + "\n");
    reader.Read(file);
    Check(reader.Tracker.Status.Activity == Activity.Idle, "Replacement after malformed data recovers");
    var unicode = Event("user.message", new { content = new string('x', 32700) + "\u00e9\u65e5" });
    File.AppendAllText(file, unicode + "\n");
    reader.Read(file);
    Check(reader.Tracker.Status.Activity == Activity.Running, "Long UTF-8 events read across chunks");
    var rawUnicode = JsonSerializer.Serialize(new
    {
        type = "user.message", data = new { content = "Resume \u00e9\u65e5" }, timestamp = DateTimeOffset.UtcNow
    }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    var rawBytes = Encoding.UTF8.GetBytes(rawUnicode + "\n");
    var splitAt = Array.IndexOf(rawBytes, (byte)0xC3) + 1;
    using (var stream = new FileStream(file, FileMode.Append))
        stream.Write(rawBytes, 0, splitAt);
    reader.Read(file);
    using (var stream = new FileStream(file, FileMode.Append))
        stream.Write(rawBytes, splitAt, rawBytes.Length - splitAt);
    reader.Read(file);
    Check(reader.Tracker.Status.Description == "Resume \u00e9\u65e5", "UTF-8 character split across separate polling reads is decoded correctly");
}
finally { File.Delete(file); }

Console.WriteLine($"\nAll {count} checks passed.");
