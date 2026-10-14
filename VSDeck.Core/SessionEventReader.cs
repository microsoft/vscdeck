using System.Text;
using System.Text.Json;

namespace VSDeck.Core;

public sealed class SessionEventReader(string id)
{
    private long offset;
    private string pending = "";
    private Decoder decoder = Encoding.UTF8.GetDecoder();
    private JsonException? fault;
    public SessionTracker Tracker { get; private set; } = new(id);

    public void Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < offset)
        {
            var workspace = Tracker.Workspace;
            var title = Tracker.Title;
            var client = Tracker.Client;
            Tracker = new(id) { Title = title, Client = client };
            Tracker.SetWorkspace(workspace);
            offset = 0;
            pending = "";
            fault = null;
            decoder = Encoding.UTF8.GetDecoder();
        }
        if (fault is not null) throw new JsonException("Session log contains a malformed event.", fault);
        stream.Position = offset;
        var bytes = new byte[32 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        int count;
        while ((count = stream.Read(bytes)) > 0)
        {
            var decoded = decoder.GetChars(bytes, 0, count, chars, 0, false);
            pending += new string(chars, 0, decoded);
            offset = stream.Position;
            int newline;
            while ((newline = pending.IndexOf('\n')) >= 0)
            {
                var line = pending[..newline].TrimEnd('\r');
                pending = pending[(newline + 1)..];
                if (line.Length == 0) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    Tracker.Apply(document.RootElement);
                }
                catch (JsonException ex)
                {
                    fault = ex;
                    throw;
                }
            }
        }
    }
}
