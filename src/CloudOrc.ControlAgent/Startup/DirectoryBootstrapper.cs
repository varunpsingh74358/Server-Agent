using CloudOrc.ControlAgent.Configuration;

namespace CloudOrc.ControlAgent.Startup;

/// <summary>
/// Ensures the ProgramData directory tree the Control Agent depends on exists before any
/// hosted service starts. Safe to call every startup - <see cref="Directory.CreateDirectory"/>
/// is a no-op when the directory is already there.
/// </summary>
public static class DirectoryBootstrapper
{
    public static void EnsureDirectories(ControlAgentOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(options.CommandsDirectory);
        Directory.CreateDirectory(options.ProcessingDirectory);
        Directory.CreateDirectory(options.CompletedDirectory);
        Directory.CreateDirectory(options.FailedDirectory);
        Directory.CreateDirectory(options.ResultsDirectory);
        Directory.CreateDirectory(options.LogsDirectory);
    }
}
