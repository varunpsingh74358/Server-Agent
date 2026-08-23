namespace CloudOrc.AgentTestServer;

/// <summary>
/// Reads operator input from the console and turns it into COMMAND/PING messages sent to
/// the connected agent. Deliberately minimal syntax - this is a test harness, not a CLI
/// product.
///
/// Supported input:
///   send &lt;script&gt;                    e.g. send Get-Date
///   send --timeout &lt;seconds&gt; &lt;script&gt;  e.g. send --timeout 5 Start-Sleep -Seconds 60
///   ping                              sends a PING to the connected agent
///   help                              prints this usage
///   exit / quit                       stops the test server
/// </summary>
public sealed class ConsoleCommandLoop(AgentSession session)
{
    /// <summary>
    /// Reads and dispatches console input until an explicit "exit"/"quit" command,
    /// cancellation, or end-of-input. Returns true only for an explicit exit request -
    /// callers must NOT treat end-of-input (e.g. stdin not being an interactive terminal
    /// at all, as happens when this tool is launched non-interactively) as equivalent to
    /// the operator asking to shut the server down; the WebSocket listener has no reason
    /// to stop just because there is nowhere to type further commands.
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        PrintUsage();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await Task.Run(Console.ReadLine, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                Console.WriteLine("[test-server] Input stream ended (no interactive console attached); the server keeps running. Stop it with Ctrl+C.");
                return false;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line is "exit" or "quit")
            {
                return true;
            }

            if (line is "help" or "?")
            {
                PrintUsage();
                continue;
            }

            if (line is "ping")
            {
                await session.SendPingAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (line.StartsWith("send ", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSendAsync(line["send ".Length..].Trim(), cancellationToken).ConfigureAwait(false);
                continue;
            }

            Console.WriteLine($"[test-server] Unrecognized input: '{line}'. Type 'help' for usage.");
        }

        return false;
    }

    private async Task HandleSendAsync(string remainder, CancellationToken cancellationToken)
    {
        var timeoutSeconds = 30;

        if (remainder.StartsWith("--timeout ", StringComparison.OrdinalIgnoreCase))
        {
            var afterFlag = remainder["--timeout ".Length..];
            var spaceIndex = afterFlag.IndexOf(' ');
            if (spaceIndex <= 0)
            {
                Console.WriteLine("[test-server] Usage: send --timeout <seconds> <script>");
                return;
            }

            var timeoutText = afterFlag[..spaceIndex];
            if (!int.TryParse(timeoutText, out timeoutSeconds) || timeoutSeconds <= 0)
            {
                Console.WriteLine($"[test-server] Invalid timeout '{timeoutText}'.");
                return;
            }

            remainder = afterFlag[(spaceIndex + 1)..].Trim();
        }

        if (remainder.Length == 0)
        {
            Console.WriteLine("[test-server] Usage: send [--timeout <seconds>] <script>");
            return;
        }

        var commandId = $"test-{Guid.NewGuid():N}"[..13];
        var sent = await session.SendCommandAsync(commandId, remainder, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        if (sent)
        {
            Console.WriteLine($"[test-server] Sent COMMAND {commandId}: {remainder} (timeout {timeoutSeconds}s)");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Commands: send <script> | send --timeout <seconds> <script> | ping | help | exit");
    }
}
