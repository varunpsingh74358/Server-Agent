using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Security;
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
        var host = new ExitCodeCapturingHost();

        try
        {
            runspace = RunspaceFactory.CreateRunspace(host);
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
            Error = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : null,
            ExitCode = host.ExitCode
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

    /// <summary>
    /// Minimal PSHost whose only real purpose is <see cref="SetShouldExit"/> - it captures
    /// the exit code a script requests via <c>exit &lt;n&gt;</c> so it can be reported back
    /// in the command result. Everything else is boilerplate the PowerShell SDK requires
    /// any host to implement; the UI members are no-ops (safe defaults) since this agent
    /// has no interactive console to render to.
    /// </summary>
    private sealed class ExitCodeCapturingHost : PSHost
    {
        private readonly Guid _instanceId = Guid.NewGuid();
        private readonly PSHostUserInterface _ui = new NoOpHostUserInterface();

        public int? ExitCode { get; private set; }

        public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
        public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;
        public override Guid InstanceId => _instanceId;
        public override string Name => "CloudOrc.ControlAgent";
        public override PSHostUserInterface UI => _ui;
        public override Version Version { get; } = new(1, 0);

        public override void EnterNestedPrompt() { }
        public override void ExitNestedPrompt() { }
        public override void NotifyBeginApplication() { }
        public override void NotifyEndApplication() { }
        public override void SetShouldExit(int exitCode) => ExitCode = exitCode;
    }

    private sealed class NoOpHostUserInterface : PSHostUserInterface
    {
        private readonly PSHostRawUserInterface _rawUi = new NoOpHostRawUserInterface();

        public override PSHostRawUserInterface RawUI => _rawUi;

        public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions) => [];
        public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice) => defaultChoice;
        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) => null!;
        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options) => null!;
        public override string ReadLine() => string.Empty;
        public override SecureString ReadLineAsSecureString() => new();
        public override void Write(string value) { }
        public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) { }
        public override void WriteDebugLine(string message) { }
        public override void WriteErrorLine(string value) { }
        public override void WriteLine() { }
        public override void WriteLine(string value) { }
        public override void WriteLine(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) { }
        public override void WriteProgress(long sourceId, ProgressRecord record) { }
        public override void WriteVerboseLine(string message) { }
        public override void WriteWarningLine(string message) { }
    }

    private sealed class NoOpHostRawUserInterface : PSHostRawUserInterface
    {
        public override ConsoleColor BackgroundColor { get; set; }
        public override Size BufferSize { get; set; } = new(120, 9999);
        public override Coordinates CursorPosition { get; set; }
        public override int CursorSize { get; set; } = 25;
        public override ConsoleColor ForegroundColor { get; set; }
        public override bool KeyAvailable => false;
        public override Size MaxPhysicalWindowSize { get; } = new(120, 9999);
        public override Size MaxWindowSize { get; } = new(120, 9999);
        public override Coordinates WindowPosition { get; set; }
        public override Size WindowSize { get; set; } = new(120, 50);
        public override string WindowTitle { get; set; } = string.Empty;

        public override void FlushInputBuffer() { }
        public override BufferCell[,] GetBufferContents(Rectangle rectangle) => new BufferCell[0, 0];
        public override KeyInfo ReadKey(ReadKeyOptions options) => default;
        public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) { }
        public override void SetBufferContents(Rectangle rectangle, BufferCell fill) { }
        public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }
    }
}
