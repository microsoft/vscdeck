using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VSCDeck.App;

internal sealed class SshCommandChannel(string host) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object processGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private Process? process;
    private Task<string>? stderr;
    private bool disposed;
    private int failures;
    private DateTimeOffset reconnectAt;
    private string lastFailure = "";

    public bool IsDisposed => Volatile.Read(ref disposed);
    public int? ConnectionProcessId
    {
        get
        {
            lock (processGate)
                return process is { HasExited: false } current ? current.Id : null;
        }
    }

    internal static TimeSpan ReconnectDelay(int failures) =>
        TimeSpan.FromSeconds(Math.Min(30, 5 * Math.Pow(2, Math.Clamp(failures - 1, 0, 3))));

    public async Task<string> Run(string[] command, string? input = null)
    {
        await gate.WaitAsync(lifetime.Token);
        int code;
        string output;
        string error;
        try
        {
            if (DateTimeOffset.UtcNow < reconnectAt)
                throw new IOException($"SSH host {host} reconnect delayed until {reconnectAt:HH:mm:ss}: {lastFailure}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var active = await Connect(timeout.Token);
                var request = JsonSerializer.Serialize(new { argv = command, input });
                await active.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
                await active.StandardInput.FlushAsync(timeout.Token);
                var line = await active.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) throw new IOException(await ExitDetail());
                using var response = JsonDocument.Parse(line);
                code = response.RootElement.GetProperty("code").GetInt32();
                output = response.RootElement.GetProperty("stdout").GetString() ?? "";
                error = response.RootElement.GetProperty("stderr").GetString() ?? "";
                failures = 0;
                lastFailure = "";
            }
            catch (Exception ex) when (ex is IOException or JsonException or Win32Exception or
                InvalidOperationException or OperationCanceledException or ObjectDisposedException)
            {
                var detail = timeout.IsCancellationRequested && !lifetime.IsCancellationRequested
                    ? "SSH connection or read-only command timed out." : ex.Message;
                StopConnection();
                if (lifetime.IsCancellationRequested) throw new OperationCanceledException(lifetime.Token);
                lastFailure = detail;
                reconnectAt = DateTimeOffset.UtcNow.Add(ReconnectDelay(++failures));
                throw new IOException($"SSH host {host}: {detail}", ex);
            }
        }
        finally { gate.Release(); }
        if (code != 0)
            throw new IOException($"Remote command exited {code}: {Core.SessionTracker.Shorten(error, 240)}");
        return output;
    }

    private async Task<Process> Connect(CancellationToken cancellation)
    {
        lock (processGate)
            if (process is { HasExited: false } existing) return existing;
        StopConnection();
        var start = new ProcessStartInfo
        {
            FileName = "ssh", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=25",
            "-o", "ConnectionAttempts=1", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=2", host })
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add("python3 -u -c " + ShellQuote(BrokerScript));
        var candidate = new Process { StartInfo = start };
        lock (processGate)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            try { candidate.Start(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                candidate.Dispose();
                throw;
            }
            process = candidate;
            stderr = candidate.StandardError.ReadToEndAsync();
        }
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        handshake.CancelAfter(TimeSpan.FromSeconds(35));
        var hello = await candidate.StandardOutput.ReadLineAsync(handshake.Token);
        if (hello is null) throw new IOException(await ExitDetail());
        using var ready = JsonDocument.Parse(hello);
        if (!ready.RootElement.GetProperty("ready").GetBoolean())
            throw new IOException("SSH status helper did not become ready.");
        return candidate;
    }

    private async Task<string> ExitDetail()
    {
        Task<string>? errors;
        lock (processGate) errors = stderr;
        var detail = errors is not null && errors.IsCompleted ? await errors : "";
        return "SSH status connection closed" + (detail.Length > 0 ? ": " + Core.SessionTracker.Shorten(detail, 240) : ".");
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private void StopConnection()
    {
        lock (processGate)
        {
            var active = process;
            process = null;
            stderr = null;
            if (active is null) return;
            try
            {
                if (!active.HasExited)
                {
                    active.Kill(entireProcessTree: true);
                    active.WaitForExit(2000);
                }
            }
            catch (Win32Exception ex)
            {
                if (!active.HasExited) AppStorage.Log($"Cannot stop VSC Deck-owned SSH client {active.Id}: {ex.Message}");
            }
            finally { active.Dispose(); }
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref disposed, true);
        lifetime.Cancel();
        StopConnection();
    }

    private const string BrokerScript = """
import json, subprocess, sys
print(json.dumps({"ready": True}), flush=True)
for line in sys.stdin:
    request = json.loads(line)
    try:
        result = subprocess.run(request["argv"], input=request.get("input"),
            stdin=subprocess.DEVNULL if request.get("input") is None else None,
            capture_output=True, text=True, encoding="utf-8", timeout=50)
        response = {"code": result.returncode, "stdout": result.stdout, "stderr": result.stderr}
    except (OSError, subprocess.TimeoutExpired) as error:
        response = {"code": 124, "stdout": "", "stderr": str(error)}
    print(json.dumps(response, ensure_ascii=False), flush=True)
""";
}
