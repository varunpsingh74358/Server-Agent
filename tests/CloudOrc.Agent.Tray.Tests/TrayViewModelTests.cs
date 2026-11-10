using CloudOrc.Agent.Tray;
using CloudOrc.Agent.Tray.Protocol;

namespace CloudOrc.Agent.Tray.Tests;

public sealed class TrayViewModelTests
{
    private static LocalStatusResponse Response(
        string state,
        bool canReconnect = false,
        string? portalUrl = null,
        string? problem = null,
        string diagnosticsId = "diag-123") => new()
    {
        State = state,
        DeviceName = "SRV-01",
        AgentVersion = "1.2.3",
        LastContactAt = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero),
        RetryStatus = "Retrying in 10s",
        Problem = problem,
        CanReconnect = canReconnect,
        DiagnosticsId = diagnosticsId,
        ProtectionSummary = "Protected - last backup 02:14, verified",
        PortalUrl = portalUrl
    };

    [Fact]
    public void NotActivated_ShowsActivationEntryOnly()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.NotActivated));

        Assert.Equal(TrayScreen.NotActivated, model.Screen);
        Assert.True(model.ShowActivate);
        Assert.True(model.ShowDiagnosticsLink);
        Assert.False(model.ShowRetry);
        Assert.False(model.ShowReconnect);
        Assert.False(model.ShowCancelActivation);
        Assert.False(model.ShowOpenPortal);
    }

    [Fact]
    public void NotActivated_HidesDiagnosticsLinkWithoutId()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.NotActivated, diagnosticsId: ""));
        Assert.False(model.ShowDiagnosticsLink);
    }

    [Fact]
    public void NotActivated_CarriesInlineActivationError()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.NotActivated), activationError: "Code expired");
        Assert.Equal("Code expired", model.ActivationError);
    }

    [Fact]
    public void Connecting_ShowsDeviceRetryStatusAndCancel()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.Connecting));

        Assert.Equal(TrayScreen.Connecting, model.Screen);
        Assert.Equal("SRV-01", model.DeviceName);
        Assert.Equal("Retrying in 10s", model.RetryStatus);
        Assert.True(model.ShowCancelActivation);
        Assert.False(model.ShowActivate);
        Assert.False(model.ShowRetry);
    }

    [Fact]
    public void Connected_ShowsStatusSummaryAndPortal()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.Connected, portalUrl: "https://portal.example.com/"));

        Assert.Equal(TrayScreen.Connected, model.Screen);
        Assert.False(model.IsOffline);
        Assert.Equal("Connected", model.StateText);
        Assert.Equal("1.2.3", model.AgentVersion);
        Assert.NotNull(model.LastContactText);
        Assert.Equal("Protected - last backup 02:14, verified", model.ProtectionSummary);
        Assert.True(model.ShowOpenPortal);
        Assert.True(model.PortalEnabled);
        Assert.Equal("https://portal.example.com/", model.PortalUrl);
        Assert.False(model.ShowRetry);
        Assert.False(model.ShowActivate);
    }

    [Fact]
    public void Offline_UsesConnectedScreenMarkedOffline()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.Offline));

        Assert.Equal(TrayScreen.Connected, model.Screen);
        Assert.True(model.IsOffline);
        Assert.Equal("Offline", model.StateText);
    }

    [Theory]
    [InlineData("http://portal.example.com/")]
    [InlineData("portal.example.com")]
    [InlineData("/relative/path")]
    [InlineData("file:///C:/evil.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void PortalButtonDisabledForNonHttpsUrls(string? url)
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.Connected, portalUrl: url));

        Assert.True(model.ShowOpenPortal);
        Assert.False(model.PortalEnabled);
        Assert.Null(model.PortalUrl);
    }

    [Theory]
    [InlineData("https://portal.example.com", true)]
    [InlineData("HTTPS://portal.example.com/x?y=1", true)]
    [InlineData("http://portal.example.com", false)]
    [InlineData("https://", false)]
    [InlineData("not a url", false)]
    public void IsHttpsAbsoluteUrl(string url, bool expected) =>
        Assert.Equal(expected, TrayViewModel.IsHttpsAbsoluteUrl(url));

    [Fact]
    public void Problem_ShowsReasonRetryAndCopyDiagnostics_NoReconnectByDefault()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.Problem, problem: "Backend unreachable"));

        Assert.Equal(TrayScreen.Problem, model.Screen);
        Assert.Equal("Backend unreachable", model.ProblemText);
        Assert.True(model.ShowRetry);
        Assert.True(model.ShowCopyDiagnostics);
        Assert.False(model.ShowReconnect);
    }

    [Fact]
    public void Problem_ShowsReconnectOnlyWhenCanReconnect()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.Problem, canReconnect: true, problem: "Authorization expired"));

        Assert.Equal(TrayScreen.Problem, model.Screen);
        Assert.True(model.ShowReconnect);
    }

    [Fact]
    public void Problem_WithReconnectRequestedAndAllowed_SwitchesToActivationEntry()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.Problem, canReconnect: true), reconnectRequested: true);

        Assert.Equal(TrayScreen.NotActivated, model.Screen);
        Assert.True(model.ShowActivate);
    }

    [Fact]
    public void Problem_WithReconnectRequestedButNotAllowed_StaysOnProblem()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.Problem, canReconnect: false), reconnectRequested: true);

        Assert.Equal(TrayScreen.Problem, model.Screen);
        Assert.False(model.ShowReconnect);
    }

    [Fact]
    public void ReconnectRequestedIsIgnoredOutsideProblemState()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.Connected, canReconnect: true), reconnectRequested: true);
        Assert.Equal(TrayScreen.Connected, model.Screen);
    }

    [Fact]
    public void Problem_WithoutDiagnosticsId_HidesCopyButton()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.Problem, diagnosticsId: ""));
        Assert.False(model.ShowCopyDiagnostics);
    }

    [Fact]
    public void Problem_WithoutReason_UsesGenericText()
    {
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.Problem, problem: null));
        Assert.False(string.IsNullOrWhiteSpace(model.ProblemText));
    }

    [Fact]
    public void UnknownState_MapsToProblem()
    {
        var model = TrayViewModel.FromResponse(Response("SOMETHING_NEW"));

        Assert.Equal(TrayScreen.Problem, model.Screen);
        Assert.Contains("SOMETHING_NEW", model.ProblemText);
        Assert.True(model.ShowRetry);
        Assert.False(model.ShowReconnect);
    }

    [Fact]
    public void ServiceUnavailable_MapsToProblemWithRetryOnly()
    {
        var model = TrayViewModel.FromResult(PipeCallResult.Unavailable(TrayViewModel.ServiceNotRunningText));

        Assert.Equal(TrayScreen.Problem, model.Screen);
        Assert.Equal("CloudOrc Agent service is not running", model.ProblemText);
        Assert.True(model.ShowRetry);
        Assert.False(model.ShowReconnect);
        Assert.False(model.ShowCopyDiagnostics);
        Assert.False(model.PortalEnabled);
    }

    [Fact]
    public void FromResult_WithResponse_DelegatesToResponseMapping()
    {
        var model = TrayViewModel.FromResult(PipeCallResult.FromResponse(Response(LocalAgentStates.Connecting)));
        Assert.Equal(TrayScreen.Connecting, model.Screen);
    }

    [Fact]
    public void Tooltip_IsTruncatedToNotifyIconLimit()
    {
        var longProblem = new string('x', 500);
        var model = TrayViewModel.FromResponse(Response(LocalAgentStates.Problem, problem: longProblem));

        Assert.True(model.TooltipText.Length <= 127);
        Assert.EndsWith("...", model.TooltipText);
    }

    [Fact]
    public void ScreenModelNeverExposesBackupControls()
    {
        // §4: no schedule/retention/sources/keys/destinations/backup/restore/file-explorer in the local UI.
        var forbidden = new[] { "schedule", "retention", "source", "key", "destination", "backup", "restore", "explore", "path" };
        var properties = typeof(ScreenModel).GetProperties().Select(p => p.Name.ToLowerInvariant());

        foreach (var property in properties)
        {
            Assert.DoesNotContain(forbidden, f => property.Contains(f, StringComparison.Ordinal));
        }
    }
}
