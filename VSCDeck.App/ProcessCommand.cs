using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VSCDeck.App;

internal static class ProcessCommand
{
    public static async Task<string> Run(string[] command, string? input, TimeSpan timeout, string description)
    {
        var start = new ProcessStartInfo
        {
            FileName = command[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception ex) { throw new Win32Exception(ex.NativeErrorCode, $"{description} could not start: {ex.Message}"); }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellation.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new IOException($"{description} timed out.");
        }
        var result = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new IOException($"{description} exited {process.ExitCode}: {Core.SessionTracker.Shorten(error, 240)}");
        return result;
    }
}
