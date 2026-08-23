using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.Agent.Contracts.Abstractions;

/// <summary>
/// Publishes a finished <see cref="CommandResult"/>. Today <c>LocalFileResultSink</c>
/// writes a JSON file; in the future a <c>SecureWebSocketResultSink</c> would stream the
/// result back to the backend. The generic PowerShell execution engine never depends on
/// which sink is in use.
/// </summary>
public interface ICommandResultSink
{
    Task WriteAsync(CommandResult result, CancellationToken cancellationToken);
}
