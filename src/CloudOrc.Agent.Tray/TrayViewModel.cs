using System.Globalization;
using CloudOrc.Agent.Tray.Protocol;

namespace CloudOrc.Agent.Tray;

/// <summary>The only screens the local UI may show (§4).</summary>
public enum TrayScreen
{
    NotActivated,
    Connecting,
    Connected,
    Problem
}

/// <summary>
/// Everything the form needs to render one screen. Pure data, produced by
/// <see cref="TrayViewModel"/>, so the screen rules are unit-testable without WinForms.
/// </summary>
public sealed class ScreenModel
{
    public required TrayScreen Screen { get; init; }

    /// <summary>Short state word for the tray tooltip ("Connected", "Offline", ...).</summary>
    public required string StateText { get; init; }

    public required string TooltipText { get; init; }

    public string DeviceName { get; init; } = string.Empty;
    public string AgentVersion { get; init; } = string.Empty;
    public string? LastContactText { get; init; }
    public string? RetryStatus { get; init; }
    public string? ProtectionSummary { get; init; }
    public string? ProblemText { get; init; }

    /// <summary>Inline error under the activation box (e.g. "Code expired").</summary>
    public string? ActivationError { get; init; }

    public string DiagnosticsId { get; init; } = string.Empty;

    public bool IsOffline { get; init; }

    public bool ShowActivate { get; init; }
    public bool ShowDiagnosticsLink { get; init; }
    public bool ShowCancelActivation { get; init; }
    public bool ShowRetry { get; init; }
    public bool ShowCopyDiagnostics { get; init; }

    /// <summary>§4: "Reconnect only if authorization expired" - mirrors canReconnect.</summary>
    public bool ShowReconnect { get; init; }

    public bool ShowOpenPortal { get; init; }

    /// <summary>Non-null only when the portal URL is an absolute https URL.</summary>
    public string? PortalUrl { get; init; }

    public bool PortalEnabled => PortalUrl is not null;
}

/// <summary>Maps an Agent Core status (or "service unreachable") to a <see cref="ScreenModel"/>.</summary>
public static class TrayViewModel
{
    public const string ServiceNotRunningText = "CloudOrc Agent service is not running";

    private const int MaxTooltipLength = 127;

    public static ScreenModel FromResult(PipeCallResult result, bool reconnectRequested = false, string? activationError = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Response is null
            ? ForServiceUnavailable(result.UnavailableReason)
            : FromResponse(result.Response, reconnectRequested, activationError);
    }

    public static ScreenModel ForServiceUnavailable(string? reason = null)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? ServiceNotRunningText : reason;
        return new ScreenModel
        {
            Screen = TrayScreen.Problem,
            StateText = "Problem",
            TooltipText = Tooltip("Problem", text),
            ProblemText = text,
            ShowRetry = true,
            // No diagnostics id is available when the service itself is unreachable.
            ShowCopyDiagnostics = false,
            ShowReconnect = false
        };
    }

    /// <param name="reconnectRequested">
    /// True after the user pressed Reconnect on a PROBLEM screen that allowed it: the form then
    /// shows the activation-code entry instead, until the agent leaves the PROBLEM state.
    /// </param>
    public static ScreenModel FromResponse(LocalStatusResponse response, bool reconnectRequested = false, string? activationError = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        var portalUrl = IsHttpsAbsoluteUrl(response.PortalUrl) ? response.PortalUrl : null;
        var lastContact = FormatLastContact(response.LastContactAt);

        switch (response.State)
        {
            case LocalAgentStates.NotActivated:
                return NotActivatedScreen(response, activationError);

            case LocalAgentStates.Connecting:
                return new ScreenModel
                {
                    Screen = TrayScreen.Connecting,
                    StateText = "Connecting",
                    TooltipText = Tooltip("Connecting", response.RetryStatus),
                    DeviceName = response.DeviceName,
                    AgentVersion = response.AgentVersion,
                    RetryStatus = response.RetryStatus,
                    DiagnosticsId = response.DiagnosticsId,
                    ShowCancelActivation = true
                };

            case LocalAgentStates.Connected:
            case LocalAgentStates.Offline:
            {
                var offline = response.State == LocalAgentStates.Offline;
                var word = offline ? "Offline" : "Connected";
                return new ScreenModel
                {
                    Screen = TrayScreen.Connected,
                    StateText = word,
                    TooltipText = Tooltip(word, response.ProtectionSummary),
                    IsOffline = offline,
                    DeviceName = response.DeviceName,
                    AgentVersion = response.AgentVersion,
                    LastContactText = lastContact,
                    ProtectionSummary = response.ProtectionSummary,
                    DiagnosticsId = response.DiagnosticsId,
                    ShowOpenPortal = true,
                    PortalUrl = portalUrl
                };
            }

            case LocalAgentStates.Problem:
                if (reconnectRequested && response.CanReconnect)
                {
                    return NotActivatedScreen(response, activationError);
                }

                var problem = string.IsNullOrWhiteSpace(response.Problem)
                    ? "The CloudOrc Agent reported a problem."
                    : response.Problem;
                return new ScreenModel
                {
                    Screen = TrayScreen.Problem,
                    StateText = "Problem",
                    TooltipText = Tooltip("Problem", problem),
                    DeviceName = response.DeviceName,
                    AgentVersion = response.AgentVersion,
                    LastContactText = lastContact,
                    ProblemText = problem,
                    DiagnosticsId = response.DiagnosticsId,
                    ShowRetry = true,
                    ShowCopyDiagnostics = !string.IsNullOrWhiteSpace(response.DiagnosticsId),
                    ShowReconnect = response.CanReconnect
                };

            default:
                var unknown = $"Unrecognized agent state '{response.State}'. Update the CloudOrc tray app.";
                return new ScreenModel
                {
                    Screen = TrayScreen.Problem,
                    StateText = "Problem",
                    TooltipText = Tooltip("Problem", null),
                    DeviceName = response.DeviceName,
                    AgentVersion = response.AgentVersion,
                    ProblemText = unknown,
                    DiagnosticsId = response.DiagnosticsId,
                    ShowRetry = true,
                    ShowCopyDiagnostics = !string.IsNullOrWhiteSpace(response.DiagnosticsId)
                };
        }
    }

    /// <summary>Only absolute https URLs may be opened from the tray.</summary>
    public static bool IsHttpsAbsoluteUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(uri.Host);

    public static string? FormatLastContact(DateTimeOffset? lastContactAt) =>
        lastContactAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static ScreenModel NotActivatedScreen(LocalStatusResponse response, string? activationError) => new()
    {
        Screen = TrayScreen.NotActivated,
        StateText = "Not activated",
        TooltipText = Tooltip("Not activated", null),
        DeviceName = response.DeviceName,
        AgentVersion = response.AgentVersion,
        DiagnosticsId = response.DiagnosticsId,
        ActivationError = activationError,
        ShowActivate = true,
        ShowDiagnosticsLink = !string.IsNullOrWhiteSpace(response.DiagnosticsId)
    };

    private static string Tooltip(string state, string? detail)
    {
        var text = string.IsNullOrWhiteSpace(detail) ? $"CloudOrc Agent - {state}" : $"CloudOrc Agent - {state}: {detail}";
        return text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 3)] + "...";
    }
}
