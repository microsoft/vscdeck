using System.Diagnostics;
using System.IO;
using System.Threading;

namespace VSCDeck.App;

internal sealed class LogWriter(string path)
{
    private readonly object gate = new();
    private string? warning;
    public string? Warning => Volatile.Read(ref warning);

    public void Write(string message)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)
                    ?? throw new IOException("The log path has no parent directory."));
                using var writer = new StreamWriter(path, append: true);
                if (warning is not null)
                    writer.WriteLine($"{DateTimeOffset.Now:O} Logging recovered after: {warning}");
                writer.WriteLine($"{DateTimeOffset.Now:O} {message}");
                writer.Flush();
                Volatile.Write(ref warning, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var detail = $"Cannot write log {path}: {ex.Message}\nLatest diagnostic: {message}";
                Volatile.Write(ref warning, detail);
                Trace.TraceError(detail);
            }
        }
    }
}
