using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Enrollment;

namespace CloudOrc.ControlAgent.Enrollment;

/// <summary>
/// §4 external/backup-only activation: "portal creates a short-lived activation code or QR/link.
/// User installs once, enters code". §12: POST /api/v1/agent/enroll {invitation_code,
/// endpoint_binding, agent_version}. The code is a one-use invitation the backend verifies
/// (hashed, expiry, attempt limits, tenant/endpoint binding) - entering a code does not by itself
/// activate a license: the server checks entitlement, organization and endpoint capability.
/// </summary>
public sealed class ActivationCodeEnrollmentClient(HttpClient httpClient)
{
    /// <summary>Normalizes "abcd 1234" → "ABCD1234"; keeps one optional hyphen. Null if not a valid code.</summary>
    public static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var normalized = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        var hyphens = normalized.Count(c => c == '-');
        var alnum = normalized.Count(char.IsAsciiLetterOrDigit);
        var valid = hyphens <= 1
                    && alnum is >= 6 and <= 8
                    && alnum + hyphens == normalized.Length
                    && !normalized.StartsWith('-') && !normalized.EndsWith('-');
        return valid ? normalized : null;
    }

    public async Task<EnrollmentOutcome> EnrollAsync(
        Uri apiBase,
        string code,
        EndpointBinding binding,
        string agentVersion,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeCode(code);
        if (normalized is null)
        {
            return EnrollmentOutcome.Failure("That activation code is not valid. Codes are 6-8 letters/numbers.");
        }

        var request = new ActivationEnrollRequest
        {
            InvitationCode = normalized,
            EndpointBinding = binding,
            AgentVersion = agentVersion
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(new Uri(apiBase, ControlPlaneRoutes.AgentEnroll), request, Transport.BackendApiClient.SnakeCaseJson, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return EnrollmentOutcome.Failure($"Could not reach CloudOrc: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return EnrollmentOutcome.Failure(response.StatusCode switch
                {
                    HttpStatusCode.BadRequest or HttpStatusCode.NotFound => "That activation code was not recognised. Check it in the CloudOrc portal.",
                    HttpStatusCode.Gone => "That activation code has expired. Create a new one in the CloudOrc portal.",
                    HttpStatusCode.Conflict => "That activation code was already used, or this device is already registered. Create a new code.",
                    HttpStatusCode.TooManyRequests => "Too many attempts. Wait a few minutes, then create a new code.",
                    HttpStatusCode.Forbidden => "This device is not allowed to activate (entitlement or organization check failed).",
                    _ => $"Activation failed ({(int)response.StatusCode})."
                });
            }

            EnrollmentResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return EnrollmentOutcome.Failure("CloudOrc returned an unreadable activation response.");
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.AgentId) || string.IsNullOrWhiteSpace(payload.ServerId)
                || string.IsNullOrWhiteSpace(payload.BackendUrl) || string.IsNullOrWhiteSpace(payload.Credential))
            {
                return EnrollmentOutcome.Failure("CloudOrc returned an incomplete activation response.");
            }

            return EnrollmentOutcome.Success(payload);
        }
    }
}
