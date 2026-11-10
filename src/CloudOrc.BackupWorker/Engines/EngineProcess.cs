using System.Diagnostics;
using System.Text;
using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.BackupWorker.Engines;

public sealed record EngineRunResult(int ExitCode, string StdoutText, IReadOnlyList<string> StderrTail, bool TimedOut, bool Cancelled);

/// <summary>
/// Runs one engine command. Secrets (repository password, tokens, keys) are passed ONLY through
/// environment variables of the child process - never on the command line, which every local
/// administrator can read from the process list (§10). The child inherits the worker's Job
/// Object, so the Resource Governor's caps apply to it automatically.
/// </summary>
public static class EngineProcess
{
    private const int StderrTailLines = 40;
    private const int MaxStdoutChars = 32 * 1024 * 1024;

    public static async Task<EngineRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        string workingDirectory,
        Action<string>? onStdoutLine,
        Action<string>? onStderrLine,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        bool captureStdout = true)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
        };

        foreach (var arg in arguments)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in environment)
        {
            if (value is null)
            {
                start.Environment.Remove(key);
            }
            else
            {
                start.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close(); // engines must never wait for interactive input

        var stdout = new StringBuilder();
        var stderrTail = new Queue<string>();

        var outTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (captureStdout && stdout.Length < MaxStdoutChars)
                {
                    stdout.AppendLine(line);
                }

                onStdoutLine?.Invoke(line);
            }
        }, CancellationToken.None);

        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                lock (stderrTail)
                {
                    stderrTail.Enqueue(SecretRedactor.Redact(line) ?? "");
                    while (stderrTail.Count > StderrTailLines)
                    {
                        stderrTail.Dequeue();
                    }
                }

                onStderrLine?.Invoke(line);
            }
        }, CancellationToken.None);

        using var timeoutCts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = cancellationToken.IsCancellationRequested;
            try
            {
                // Killing the engine also ends any VSS snapshot it holds (auto-release shadow copies
                // are deleted when the requester process goes away) - this is how the snapshot
                // lifetime limit is enforced.
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        string[] tail;
        lock (stderrTail)
        {
            tail = stderrTail.ToArray();
        }

        return new EngineRunResult(process.ExitCode, stdout.ToString(), tail, timedOut, cancelled);
    }
}
