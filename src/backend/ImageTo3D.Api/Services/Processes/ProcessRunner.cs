using System.Diagnostics;
using System.Text;

namespace ImageTo3D.Api.Services.Processes;

/// <summary>
/// A local executable started with an explicit argument list (never through a shell), optional
/// bytes on stdin, size-capped stdout/stderr, and a hard time limit.
/// </summary>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    byte[]? StandardInput = null,
    int MaxStandardOutputBytes = 1024 * 1024,
    int MaxStandardErrorBytes = 64 * 1024,
    IReadOnlyDictionary<string, string?>? Environment = null);

/// <param name="ExitCode">-1 when the process was killed (timeout or cancellation).</param>
/// <param name="StandardError">Last <see cref="ProcessSpec.MaxStandardErrorBytes"/> bytes, decoded as UTF-8.</param>
public sealed record ProcessResult(int ExitCode, byte[] StandardOutput, string StandardError, bool TimedOut, TimeSpan Duration);

public interface IProcessRunner
{
    /// <summary>Runs the process to completion. Cancellation kills the whole process tree and rethrows.</summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);
}

public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in spec.Arguments)
            info.ArgumentList.Add(argument);
        foreach (var (key, value) in spec.Environment ?? new Dictionary<string, string?>())
            info.Environment[key] = value;

        var started = Stopwatch.StartNew();
        using var process = new Process { StartInfo = info };
        process.Start();
        logger.LogDebug("Started {FileName} as process {ProcessId}", Path.GetFileName(spec.FileName), process.Id);

        var stdout = ReadCappedAsync(process.StandardOutput.BaseStream, spec.MaxStandardOutputBytes, keepTail: false);
        var stderr = ReadCappedAsync(process.StandardError.BaseStream, spec.MaxStandardErrorBytes, keepTail: true);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(spec.Timeout);
        var timedOut = false;
        try
        {
            try
            {
                await using var stdin = process.StandardInput.BaseStream;
                if (spec.StandardInput is { } input)
                    await stdin.WriteAsync(input, timeout.Token);
            }
            catch (IOException)
            {
                // The process closed stdin early (it exited or crashed); its exit code tells why.
            }

            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
                throw;
            timedOut = true;
        }

        var output = await stdout;
        var error = await stderr;
        return new ProcessResult(timedOut ? -1 : process.ExitCode, output, Encoding.UTF8.GetString(error), timedOut, started.Elapsed);
    }

    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Could not kill process {ProcessId}", process.Id);
        }
    }

    /// <summary>Drains the stream to the end (so the child never blocks on a full pipe) but keeps at most <paramref name="max"/> bytes.</summary>
    private static async Task<byte[]> ReadCappedAsync(Stream stream, int max, bool keepTail)
    {
        var kept = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            if (!keepTail)
            {
                var room = max - (int)kept.Length;
                if (room > 0)
                    kept.Write(buffer, 0, Math.Min(room, read));
                continue;
            }

            kept.Write(buffer, 0, read);
            if (kept.Length > 2L * max)
            {
                var tail = kept.ToArray()[^max..];
                kept = new MemoryStream();
                kept.Write(tail);
            }
        }

        var all = kept.ToArray();
        return all.Length > max ? all[^max..] : all;
    }
}
