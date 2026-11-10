using System.Diagnostics;
using System.Drawing;
using CloudOrc.Agent.Tray.Protocol;

namespace CloudOrc.Agent.Tray;

/// <summary>
/// Owns the tray icon, the status window and the polling loop. All pipe I/O runs on the
/// thread pool; results are marshalled back to the UI thread before touching any control.
/// Polls every 5s while the window is visible, every 30s otherwise (tooltip/state only).
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan VisiblePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HiddenPollInterval = TimeSpan.FromSeconds(30);

    private readonly StatusPipeClient _client;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _openPortalItem;
    private readonly StatusForm _form;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly CancellationTokenSource _shutdown = new();

    private ScreenModel _model = TrayViewModel.ForServiceUnavailable();
    private bool _pollInFlight;
    private bool _reconnectRequested;
    private string? _lastActivationError;

    public TrayApplicationContext(StatusPipeClient client)
    {
        _client = client;

        // Creating the first control installs WinForms' synchronization context on this thread.
        _form = new StatusForm();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _form.ActivateRequested += OnActivateRequested;
        _form.CancelActivationRequested += (_, _) => SendAction(LocalStatusProtocol.ActionCancelActivation);
        _form.RetryRequested += (_, _) => SendAction(LocalStatusProtocol.ActionRetry);
        _form.ReconnectRequested += (_, _) =>
        {
            _reconnectRequested = true;
            _lastActivationError = null;
            _form.Render(TrayViewModel.FromResponse(
                new LocalStatusResponse
                {
                    State = LocalAgentStates.Problem,
                    CanReconnect = true,
                    DeviceName = _model.DeviceName,
                    AgentVersion = _model.AgentVersion,
                    DiagnosticsId = _model.DiagnosticsId
                },
                reconnectRequested: true));
        };
        _form.CopyDiagnosticsRequested += (_, _) => CopyDiagnosticsId();
        _form.OpenPortalRequested += (_, _) => OpenPortal();
        _form.VisibleChanged += (_, _) =>
        {
            UpdatePollInterval();
            if (_form.Visible)
            {
                PollNow();
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open status", null, (_, _) => ShowStatusWindow());
        _openPortalItem = new ToolStripMenuItem("Open CloudOrc Portal", null, (_, _) => OpenPortal()) { Enabled = false };
        menu.Items.Add(_openPortalItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = _model.TooltipText,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatusWindow();

        _pollTimer = new System.Windows.Forms.Timer();
        _pollTimer.Tick += (_, _) => PollNow();
        UpdatePollInterval();
        _pollTimer.Start();

        Apply(_model);
        PollNow();
    }

    protected override void ExitThreadCore()
    {
        _shutdown.Cancel();
        _pollTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _form.Dispose();
        _pollTimer.Dispose();
        base.ExitThreadCore();
    }

    private void ShowStatusWindow()
    {
        if (!_form.Visible)
        {
            _form.Show();
        }

        if (_form.WindowState == FormWindowState.Minimized)
        {
            _form.WindowState = FormWindowState.Normal;
        }

        _form.Activate();
    }

    private void UpdatePollInterval() =>
        _pollTimer.Interval = (int)(_form.Visible ? VisiblePollInterval : HiddenPollInterval).TotalMilliseconds;

    private void PollNow()
    {
        if (_pollInFlight || _shutdown.IsCancellationRequested)
        {
            return;
        }

        _pollInFlight = true;
        _ = Task.Run(async () =>
        {
            PipeCallResult result;
            try
            {
                result = await _client.GetStatusAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _ui.Post(_ => _pollInFlight = false, null);
            }

            _ui.Post(_ => OnStatusResult(result), null);
        });
    }

    private void OnStatusResult(PipeCallResult result)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        // The reconnect (re-activation) entry stays up only while the agent is still in PROBLEM.
        if (result.Response?.State is { } state && state != LocalAgentStates.Problem)
        {
            _reconnectRequested = false;
        }

        if (result.Response?.State is not LocalAgentStates.NotActivated and not LocalAgentStates.Problem)
        {
            _lastActivationError = null;
        }

        Apply(TrayViewModel.FromResult(result, _reconnectRequested, _lastActivationError));
    }

    private void Apply(ScreenModel model)
    {
        _model = model;
        _notifyIcon.Text = model.TooltipText;
        _openPortalItem.Enabled = model.PortalEnabled;
        _form.Render(model);
    }

    private void OnActivateRequested(object? sender, string rawCode)
    {
        if (!ActivationCodeFormat.TryNormalize(rawCode, out var code))
        {
            _form.ShowActivationError("Enter the 6–8 character code from the CloudOrc portal (letters and digits, e.g. ABCD-1234).");
            return;
        }

        _form.SetActivationBusy(true);
        _form.ShowActivationError(null);

        _ = Task.Run(async () =>
        {
            PipeCallResult result;
            try
            {
                result = await _client.SendAsync(
                    new LocalStatusRequest { Action = LocalStatusProtocol.ActionActivate, Code = code },
                    _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ui.Post(_ => OnActivateResult(result), null);
        });
    }

    private void OnActivateResult(PipeCallResult result)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        _form.SetActivationBusy(false);

        if (result.Response is { Ok: false } failed)
        {
            _lastActivationError = string.IsNullOrWhiteSpace(failed.Error) ? "Activation failed." : failed.Error;
            _form.ShowActivationError(_lastActivationError);
            return;
        }

        if (result.Response is null)
        {
            Apply(TrayViewModel.FromResult(result));
            return;
        }

        _reconnectRequested = false;
        _lastActivationError = null;
        Apply(TrayViewModel.FromResult(result));
    }

    private void SendAction(string action)
    {
        _ = Task.Run(async () =>
        {
            PipeCallResult result;
            try
            {
                result = await _client.SendAsync(new LocalStatusRequest { Action = action }, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ui.Post(_ => OnStatusResult(result), null);
        });
    }

    private void CopyDiagnosticsId()
    {
        if (string.IsNullOrWhiteSpace(_model.DiagnosticsId))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_model.DiagnosticsId);
            _notifyIcon.ShowBalloonTip(2000, "CloudOrc Agent", "Diagnostics ID copied to the clipboard.", ToolTipIcon.Info);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard temporarily locked by another app - harmless, the user can retry.
        }
    }

    private void OpenPortal()
    {
        var url = _model.PortalUrl;
        if (!TrayViewModel.IsHttpsAbsoluteUrl(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _notifyIcon.ShowBalloonTip(3000, "CloudOrc Agent", "Could not open the browser.", ToolTipIcon.Warning);
        }
    }
}
