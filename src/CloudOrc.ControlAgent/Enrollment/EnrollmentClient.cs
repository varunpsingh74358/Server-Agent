using System.Net.Http.Json;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Enrollment;

namespace CloudOrc.ControlAgent.Enrollment;

/// <summary>
/// Redeems a one-time enrollment token: decodes it locally to find the enrollment
/// endpoint (no fixed/hardcoded backend or bootstrap host anywhere in this class), posts
/// the embedded single-use secret to it, and returns the backend connection details the
/// endpoint hands back. Never throws for an expected failure mode (bad token, unreachable
/// endpoint, rejected secret) - those all come back as <see cref="EnrollmentOutcome.Failure"/>
/// so the caller (the <c>enroll</c> CLI mode) can print a clean message and exit non-zero
/// without an unhandled exception.
/// </summary>
public sealed class EnrollmentClient(HttpClient httpClient)
{
    public async Task<EnrollmentOutcome> EnrollAsync(
        string token,
        string machineId,
        string machineName,
        string agentVersion,
        CancellationToken cancellationToken)
    {
        if (!EnrollmentToken.TryDecode(token, out var decoded))
        {
            return EnrollmentOutcome.Failure("The provided enrollment token is not in a recognized format.");
        }

        var request = new EnrollmentRequest
        {
            Secret = decoded!.Secret,
            MachineId = machineId,
            MachineName = machineName,
            AgentVersion = agentVersion
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(decoded.EnrollmentUrl, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return EnrollmentOutcome.Failure($"Could not reach the enrollment endpoint: {DescribeWithInnerExceptions(ex)}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                var suffix = string.IsNullOrWhiteSpace(body) ? string.Empty : $": {body}";
                return EnrollmentOutcome.Failure($"Enrollment was rejected ({(int)response.StatusCode} {response.ReasonPhrase}){suffix}");
            }

            EnrollmentResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return EnrollmentOutcome.Failure($"Enrollment endpoint returned an unreadable response: {ex.Message}");
            }

            if (payload is null
                || string.IsNullOrWhiteSpace(payload.AgentId)
                || string.IsNullOrWhiteSpace(payload.ServerId)
                || string.IsNullOrWhiteSpace(payload.BackendUrl)
                || string.IsNullOrWhiteSpace(payload.Credential))
            {
                return EnrollmentOutcome.Failure("Enrollment endpoint returned an incomplete response.");
            }

            return EnrollmentOutcome.Success(payload);
        }
    }

    /// <summary>
    /// .NET's top-level message for a failed TLS handshake is the generic
    /// "The SSL connection could not be established, see inner exception." - the actually
    /// useful detail (untrusted certificate chain, hostname mismatch, protocol mismatch,
    /// a proxy/firewall resetting the connection, etc.) lives one or two
    /// <see cref="Exception.InnerException"/> levels down. Surfacing the whole chain here
    /// is what turns an unhelpful "could not connect" message into something an operator
    /// can actually act on without needing to attach a debugger.
    /// </summary>
    private static string DescribeWithInnerExceptions(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (messages.Count == 0 || !string.Equals(messages[^1], current.Message, StringComparison.Ordinal))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" -> ", messages);
    }

    private static async Task<string?> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
