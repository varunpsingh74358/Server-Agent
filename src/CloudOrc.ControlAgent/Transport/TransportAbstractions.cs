namespace CloudOrc.ControlAgent.Transport;

/// <summary>Routes typed (backup/restore/agent) COMMAND messages off the receive loop.</summary>
public interface ITypedCommandRouter
{
    /// <summary>Returns true when <paramref name="commandType"/> is typed and was taken over.</summary>
    bool TryRoute(string json, string? commandType);
}

/// <summary>Supplies the HELLO capability profile and installed engine versions.</summary>
public interface IAgentCapabilitiesProvider
{
    IReadOnlyList<string> Capabilities { get; }

    IReadOnlyDictionary<string, string>? EngineVersions { get; }
}

/// <summary>Session renewal seam (Windows device key implementation in production).</summary>
public interface ISessionRenewer
{
    bool IsAvailable { get; }

    Task<RenewalOutcome> RenewAsync(CancellationToken cancellationToken);
}
