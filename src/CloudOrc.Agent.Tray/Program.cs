namespace CloudOrc.Agent.Tray;

/// <summary>
/// Optional CloudOrc tray/status app (CO-DEV-BKP-001 §4). A separate per-user process that
/// talks to the Agent Core service only through the local status pipe. Headless servers do
/// not need it; the service never depends on it.
/// </summary>
internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\CloudOrc.Agent.Tray";

    [STAThread]
    private static int Main(string[] args)
    {
        // Deep-link activation (cloudorc://activate?code=...): a short-lived, approval-only run
        // that does not start the tray icon and ignores the single-instance rule.
        if (args.Length > 0 && DeepLink.TryGetActivationCode(args[0]) is { } code)
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            return DeepLink.RunActivation(
                code,
                StatusPipeClient.CreateDefault(),
                (title, text) => MessageBox.Show(text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes,
                (title, text, error) => MessageBox.Show(text, title, MessageBoxButtons.OK, error ? MessageBoxIcon.Warning : MessageBoxIcon.Information));
        }

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // Already running in this user session.
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var context = new TrayApplicationContext(StatusPipeClient.CreateDefault());
        Application.Run(context);

        GC.KeepAlive(mutex);
        return 0;
    }
}
