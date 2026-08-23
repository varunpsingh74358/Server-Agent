using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.Agent.Contracts.Execution;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Services;

/// <summary>
/// Generic PowerShell execution engine built on the PowerShell SDK
/// (System.Management.Automation, via the Microsoft.PowerShell.SDK package). It accepts
/// any script text - there is no branching on the content of the script anywhere in this
/// class. A fresh Runspace is created per invocation, which is a small amount of overhead
/// but guarantees one command's variables/functions/state can never leak into the next -
/// an intentional trade-off given commands run sequentially.
///
/// Timeout and cancellation both use PowerShell's own <see cref="PowerShell.Stop"/>,
/// which asks the running pipeline to terminate rather than abandoning a thread. A
/// terminating exception, a non-terminating error written to the error stream, and a
/// stopped/timed-out pipeline are all distinguished so the caller gets an accurate status.
/// </summary>
public sealed class PowerShellCommandExecutor(ILogger<PowerShellCommandExecutor> logger) : IPowerShellExecutor
{
    public async Task<PowerShellExecutionOutcome> ExecuteAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var output = new List<string>();
        var errors = new List<string>();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        Runspace? runspace = null;
        PowerShell? shell = null;

        try
        {
            runspace = RunspaceFactory.CreateRunspace();
            runspace.Open();

            shell = PowerShell.Create();
            shell.Runspace = runspace;
            shell.AddScript(script);

            var outputCollection = new PSDataCollection<PSObject>();
            outputCollection.DataAdded += (sender, args) =>
            {
                var collection = (PSDataCollection<PSObject>)sender!;
                var item = collection[args.Index];
                output.Add(item?.ToString() ?? string.Empty);
            };

            shell.Streams.Error.DataAdded += (sender, args) =>
            {
                var collection = (PSDataCollection<ErrorRecord>)sender!;
                errors.Add(collection[args.Index].ToString());
            };

            // The stop registration must be wired up only once the pipeline has actually
            // started (i.e. after BeginInvoke returns). Registering it earlier creates a
            // race: if cancellation fires while the pipeline is still NotStarted (runspace
            // creation on a cold PowerShell SDK can itself take a noticeable amount of
            // time), Stop() has nothing to stop and is never retried once execution
            // actually begins - the script then runs to completion regardless of the
            // caller's cancellation.
            var asyncResult = shell.BeginInvoke<PSObject, PSObject>(null, outputCollection);

            void StopIfRequested()
            {
                try
                {
                    shell.Stop();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Ignoring exception while stopping a PowerShell pipeline that was already completing.");
                }
            }

            await using var stopRegistration = linkedCts.Token.Register(StopIfRequested);

            // Covers the residual race between BeginInvoke returning and the registration
            // above taking effect.
            if (linkedCts.IsCancellationRequested)
            {
                StopIfRequested();
            }

            try
            {
                await Task.Factory.FromAsync(asyncResult, shell.EndInvoke).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A terminating error in the script (or a stop signal) surfaces here.
                errors.Add(ex.Message);
            }
        }
        finally
        {
            shell?.Dispose();
            runspace?.Dispose();
            stopwatch.Stop();
        }

        var completedAt = DateTimeOffset.UtcNow;
        var status = DetermineStatus(cancellationToken, timeoutCts, errors);

        return new PowerShellExecutionOutcome
        {
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds,
            Output = output,
            Error = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : null
        };
    }

    private static CommandStatus DetermineStatus(
        CancellationToken callerToken,
        CancellationTokenSource timeoutCts,
        List<string> errors)
    {
        if (callerToken.IsCancellationRequested)
        {
            return CommandStatus.Cancelled;
        }

        if (timeoutCts.IsCancellationRequested)
        {
            return CommandStatus.Timeout;
        }

        return errors.Count > 0 ? CommandStatus.Failed : CommandStatus.Success;
    }
}
