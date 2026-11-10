namespace CloudOrc.ControlAgent.Configuration;

/// <summary>
/// "Activation" section - where an UN-enrolled agent sends an activation code (§4 external /
/// backup-only devices). Written by the installer from the portal download it came from; never
/// hardcoded in source. Ignored once the agent is enrolled.
/// </summary>
public sealed class ActivationOptions
{
    public const string SectionName = "Activation";

    /// <summary>HTTPS base URL of the CloudOrc control plane, e.g. https://api.example/</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>Portal URL shown by the tray before activation (optional).</summary>
    public string? PortalUrl { get; set; }

    /// <summary>LOCAL TESTING ONLY: allow an http:// activation endpoint.</summary>
    public bool DevelopmentAllowInsecureHttp { get; set; }
}
