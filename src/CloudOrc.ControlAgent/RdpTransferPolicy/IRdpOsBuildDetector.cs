namespace CloudOrc.ControlAgent.RdpTransferPolicy;

/// <summary>
/// Detects the running Windows build/UBR and whether it supports the directional
/// ("plain text only") clipboard redirection policies. Abstracted behind an interface so
/// <see cref="WindowsRdpTransferPolicyManager"/>'s capability-gating logic (Server 2019 vs.
/// Server 2022 vs. an under-patched Server 2022) is unit-testable without a matching real
/// OS for every scenario.
/// </summary>
public interface IRdpOsBuildDetector
{
    RdpOsBuildInfo DetectCurrent();
}
