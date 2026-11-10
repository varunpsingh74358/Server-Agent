using System.Drawing;

namespace CloudOrc.Agent.Tray;

/// <summary>
/// Small fixed-size status window showing exactly one of the four §4 screens. It renders a
/// <see cref="ScreenModel"/> and raises intent events; it never talks to the pipe itself.
/// Intentionally has no controls for schedule, retention, sources, keys, destinations,
/// backup/restore or file browsing - those belong to the CloudOrc portal (§4).
/// </summary>
internal sealed class StatusForm : Form
{
    private readonly Panel _notActivatedPanel;
    private readonly Panel _connectingPanel;
    private readonly Panel _connectedPanel;
    private readonly Panel _problemPanel;

    // Not activated
    private readonly TextBox _codeBox;
    private readonly Button _activateButton;
    private readonly Label _activationErrorLabel;
    private readonly LinkLabel _diagnosticsLink;

    // Connecting
    private readonly Label _connectingDeviceLabel;
    private readonly Label _retryStatusLabel;
    private readonly Button _cancelActivationButton;

    // Connected / Offline
    private readonly Label _connectionStateLabel;
    private readonly Label _lastContactLabel;
    private readonly Label _versionLabel;
    private readonly Label _protectionLabel;
    private readonly Button _openPortalButton;

    // Problem
    private readonly Label _problemLabel;
    private readonly Button _retryButton;
    private readonly Button _copyDiagnosticsButton;
    private readonly Button _reconnectButton;

    private ScreenModel? _current;

    public StatusForm()
    {
        Text = "CloudOrc Agent";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(380, 230);
        Icon = SystemIcons.Shield;
        Font = new Font("Segoe UI", 9F);

        var title = new Label
        {
            Text = "CloudOrc",
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            AutoSize = false,
            Location = new Point(16, 10),
            Size = new Size(348, 32)
        };
        Controls.Add(title);

        // ---------------------------------------------------------------- Not activated
        _notActivatedPanel = NewPanel();
        _notActivatedPanel.Controls.Add(new Label
        {
            Text = "Enter the activation code shown in the CloudOrc portal:",
            AutoSize = false,
            Location = new Point(0, 0),
            Size = new Size(348, 20)
        });
        _codeBox = new TextBox
        {
            Location = new Point(0, 26),
            Size = new Size(220, 27),
            MaxLength = 16,
            CharacterCasing = CharacterCasing.Upper,
            Font = new Font("Consolas", 12F)
        };
        _activateButton = new Button { Text = "Activate", Location = new Point(232, 25), Size = new Size(100, 29) };
        _activateButton.Click += (_, _) => ActivateRequested?.Invoke(this, _codeBox.Text);
        _codeBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _activateButton.Enabled)
            {
                e.SuppressKeyPress = true;
                ActivateRequested?.Invoke(this, _codeBox.Text);
            }
        };
        _activationErrorLabel = new Label
        {
            ForeColor = Color.Firebrick,
            AutoSize = false,
            Location = new Point(0, 62),
            Size = new Size(348, 40)
        };
        _diagnosticsLink = new LinkLabel { Text = "Diagnostics", AutoSize = true, Location = new Point(0, 110) };
        _diagnosticsLink.LinkClicked += (_, _) => CopyDiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        _notActivatedPanel.Controls.AddRange([_codeBox, _activateButton, _activationErrorLabel, _diagnosticsLink]);

        // ---------------------------------------------------------------- Connecting
        _connectingPanel = NewPanel();
        _connectingDeviceLabel = new Label { AutoSize = false, Location = new Point(0, 0), Size = new Size(348, 20) };
        var progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Location = new Point(0, 28),
            Size = new Size(348, 16)
        };
        _retryStatusLabel = new Label { AutoSize = false, Location = new Point(0, 52), Size = new Size(348, 40) };
        _cancelActivationButton = new Button { Text = "Cancel activation", Location = new Point(0, 100), Size = new Size(140, 29) };
        _cancelActivationButton.Click += (_, _) => CancelActivationRequested?.Invoke(this, EventArgs.Empty);
        _connectingPanel.Controls.AddRange([_connectingDeviceLabel, progress, _retryStatusLabel, _cancelActivationButton]);

        // ---------------------------------------------------------------- Connected / Offline
        _connectedPanel = NewPanel();
        _connectionStateLabel = new Label
        {
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            AutoSize = false,
            Location = new Point(0, 0),
            Size = new Size(348, 24)
        };
        _lastContactLabel = new Label { AutoSize = false, Location = new Point(0, 28), Size = new Size(348, 20) };
        _versionLabel = new Label { AutoSize = false, Location = new Point(0, 50), Size = new Size(348, 20) };
        _protectionLabel = new Label { AutoSize = false, Location = new Point(0, 72), Size = new Size(348, 36) };
        _openPortalButton = new Button { Text = "Open CloudOrc Portal", Location = new Point(0, 112), Size = new Size(170, 29) };
        _openPortalButton.Click += (_, _) => OpenPortalRequested?.Invoke(this, EventArgs.Empty);
        _connectedPanel.Controls.AddRange([_connectionStateLabel, _lastContactLabel, _versionLabel, _protectionLabel, _openPortalButton]);

        // ---------------------------------------------------------------- Problem
        _problemPanel = NewPanel();
        _problemLabel = new Label
        {
            ForeColor = Color.Firebrick,
            AutoSize = false,
            Location = new Point(0, 0),
            Size = new Size(348, 100)
        };
        _retryButton = new Button { Text = "Retry", Location = new Point(0, 112), Size = new Size(80, 29) };
        _retryButton.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
        _copyDiagnosticsButton = new Button { Text = "Copy diagnostics ID", Location = new Point(88, 112), Size = new Size(150, 29) };
        _copyDiagnosticsButton.Click += (_, _) => CopyDiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        _reconnectButton = new Button { Text = "Reconnect", Location = new Point(246, 112), Size = new Size(96, 29) };
        _reconnectButton.Click += (_, _) => ReconnectRequested?.Invoke(this, EventArgs.Empty);
        _problemPanel.Controls.AddRange([_problemLabel, _retryButton, _copyDiagnosticsButton, _reconnectButton]);

        Controls.AddRange([_notActivatedPanel, _connectingPanel, _connectedPanel, _problemPanel]);
    }

    public event EventHandler<string>? ActivateRequested;
    public event EventHandler? CancelActivationRequested;
    public event EventHandler? RetryRequested;
    public event EventHandler? ReconnectRequested;
    public event EventHandler? CopyDiagnosticsRequested;
    public event EventHandler? OpenPortalRequested;

    /// <summary>Disables the activation controls while an activate call is in flight.</summary>
    public void SetActivationBusy(bool busy)
    {
        _activateButton.Enabled = !busy;
        _codeBox.Enabled = !busy;
        _activateButton.Text = busy ? "Activating..." : "Activate";
    }

    public void ShowActivationError(string? message) => _activationErrorLabel.Text = message ?? string.Empty;

    public void Render(ScreenModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var screenChanged = _current?.Screen != model.Screen;
        _current = model;

        _notActivatedPanel.Visible = model.Screen == TrayScreen.NotActivated;
        _connectingPanel.Visible = model.Screen == TrayScreen.Connecting;
        _connectedPanel.Visible = model.Screen == TrayScreen.Connected;
        _problemPanel.Visible = model.Screen == TrayScreen.Problem;

        switch (model.Screen)
        {
            case TrayScreen.NotActivated:
                _activateButton.Visible = model.ShowActivate;
                _diagnosticsLink.Visible = model.ShowDiagnosticsLink;
                if (model.ActivationError is not null)
                {
                    _activationErrorLabel.Text = model.ActivationError;
                }
                else if (screenChanged)
                {
                    _activationErrorLabel.Text = string.Empty;
                }

                break;

            case TrayScreen.Connecting:
                _connectingDeviceLabel.Text = string.IsNullOrWhiteSpace(model.DeviceName)
                    ? "Registering this device..."
                    : $"Registering {model.DeviceName}...";
                _retryStatusLabel.Text = model.RetryStatus ?? string.Empty;
                _cancelActivationButton.Visible = model.ShowCancelActivation;
                break;

            case TrayScreen.Connected:
                _connectionStateLabel.Text = model.IsOffline ? "Offline" : "Connected";
                _connectionStateLabel.ForeColor = model.IsOffline ? Color.DarkOrange : Color.ForestGreen;
                _lastContactLabel.Text = $"Last contact: {model.LastContactText ?? "never"}";
                _versionLabel.Text = $"Agent version: {model.AgentVersion}";
                _protectionLabel.Text = model.ProtectionSummary ?? string.Empty;
                _openPortalButton.Visible = model.ShowOpenPortal;
                _openPortalButton.Enabled = model.PortalEnabled;
                break;

            case TrayScreen.Problem:
                _problemLabel.Text = model.ProblemText ?? string.Empty;
                _retryButton.Visible = model.ShowRetry;
                _copyDiagnosticsButton.Visible = model.ShowCopyDiagnostics;
                _reconnectButton.Visible = model.ShowReconnect;
                break;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window only hides it; the tray icon keeps running until "Exit".
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    private static Panel NewPanel() => new()
    {
        Location = new Point(16, 50),
        Size = new Size(348, 165),
        Visible = false
    };
}
