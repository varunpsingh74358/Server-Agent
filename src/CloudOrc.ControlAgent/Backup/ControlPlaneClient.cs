using System.Net;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.Transport;

namespace CloudOrc.ControlAgent.Backup;

public enum CallStatus
{
    Ok,

    /// <summary>Retry later: network, timeout, 429, 5xx, 401 (renewable).</summary>
    Transient,

    /// <summary>The backend definitively said no (403/404/409/410/422...).</summary>
    Rejected
}

public sealed record CallResult<T>(CallStatus Status, T? Value, TimeSpan? RetryAfter, string Detail, HttpStatusCode? Code);

/// <summary>Typed wrappers over the §12 admission / lease / grant endpoints.</summary>
public interface IBackupControlPlane
{
    bool IsConfigured { get; }

    Task<CallResult<AdmissionResponse>> RequestAdmissionAsync(AdmissionRequest request, CancellationToken cancellationToken);

    Task<CallResult<AdmissionLease>> RenewLeaseAsync(string leaseId, LeaseRenewRequest request, CancellationToken cancellationToken);

    Task ReleaseLeaseAsync(string leaseId, LeaseRenewRequest request, CancellationToken cancellationToken);

    Task<CallResult<StorageGrant>> RequestGrantAsync(StorageGrantRequest request, CancellationToken cancellationToken);
}

public sealed class BackupControlPlane(BackendApiClient api) : IBackupControlPlane
{
    public bool IsConfigured => api.IsConfigured;

    public async Task<CallResult<AdmissionResponse>> RequestAdmissionAsync(AdmissionRequest request, CancellationToken cancellationToken) =>
        Map<AdmissionResponse>(await api.PostJsonAsync(ControlPlaneRoutes.AdmissionRequest, request, cancellationToken).ConfigureAwait(false));

    public async Task<CallResult<AdmissionLease>> RenewLeaseAsync(string leaseId, LeaseRenewRequest request, CancellationToken cancellationToken) =>
        Map<AdmissionLease>(await api.PostJsonAsync(ControlPlaneRoutes.LeaseRenew(leaseId), request, cancellationToken).ConfigureAwait(false));

    public async Task ReleaseLeaseAsync(string leaseId, LeaseRenewRequest request, CancellationToken cancellationToken) =>
        await api.PostJsonAsync(ControlPlaneRoutes.LeaseRelease(leaseId), request, cancellationToken).ConfigureAwait(false);

    public async Task<CallResult<StorageGrant>> RequestGrantAsync(StorageGrantRequest request, CancellationToken cancellationToken) =>
        Map<StorageGrant>(await api.PostJsonAsync(ControlPlaneRoutes.StorageGrants, request, cancellationToken).ConfigureAwait(false));

    private static CallResult<T> Map<T>(ApiResponse response) where T : class
    {
        if (response.IsSuccess)
        {
            try
            {
                var value = response.Read<T>(BackendApiClient.SnakeCaseJson);
                return value is null
                    ? new CallResult<T>(CallStatus.Transient, null, null, "Empty response body.", response.StatusCode)
                    : new CallResult<T>(CallStatus.Ok, value, null, "OK", response.StatusCode);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return new CallResult<T>(CallStatus.Transient, null, null, $"Unreadable response: {ex.Message}", response.StatusCode);
            }
        }

        var status = response.IsTransient ? CallStatus.Transient : CallStatus.Rejected;
        return new CallResult<T>(status, null, response.RetryAfter, response.Describe(), response.StatusCode);
    }
}
