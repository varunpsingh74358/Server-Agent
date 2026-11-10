namespace CloudOrc.Agent.Updater;

/// <summary>
/// Recursive copy for the pre-install backup, and an exact restore: every backed-up file is
/// copied back over the target, and any file or directory in the target that is not in the
/// backup (i.e. added by the failed install) is deleted, so the rolled-back tree matches the
/// pre-update tree exactly.
/// </summary>
public sealed class DirectoryBackup : IDirectoryBackup
{
    private const int IoAttempts = 5;
    private readonly TimeSpan _retryDelay;

    public DirectoryBackup() : this(TimeSpan.FromSeconds(1))
    {
    }

    internal DirectoryBackup(TimeSpan retryDelay) => _retryDelay = retryDelay;

    public void Backup(string sourceDirectory, string backupDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Install directory '{sourceDirectory}' does not exist.");
        }

        if (Directory.Exists(backupDirectory))
        {
            Directory.Delete(backupDirectory, recursive: true);
        }

        CopyTree(sourceDirectory, backupDirectory);
    }

    public void Restore(string backupDirectory, string targetDirectory)
    {
        if (!Directory.Exists(backupDirectory))
        {
            throw new DirectoryNotFoundException($"Rollback backup '{backupDirectory}' does not exist.");
        }

        Directory.CreateDirectory(targetDirectory);
        CopyTree(backupDirectory, targetDirectory);
        DeleteExtra(backupDirectory, targetDirectory);
    }

    public void DeleteBackup(string backupDirectory)
    {
        if (Directory.Exists(backupDirectory))
        {
            Directory.Delete(backupDirectory, recursive: true);
        }
    }

    private void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Retry(() => File.Copy(file, target, overwrite: true));
        }
    }

    private void DeleteExtra(string backupRoot, string targetRoot)
    {
        foreach (var file in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories).ToList())
        {
            var relative = Path.GetRelativePath(targetRoot, file);
            if (!File.Exists(Path.Combine(backupRoot, relative)))
            {
                Retry(() =>
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                });
            }
        }

        // Deepest first, so a nested extra directory is removed before its parent is checked.
        var directories = Directory.EnumerateDirectories(targetRoot, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length)
            .ToList();
        foreach (var directory in directories)
        {
            var relative = Path.GetRelativePath(targetRoot, directory);
            if (!Directory.Exists(Path.Combine(backupRoot, relative)) && Directory.Exists(directory))
            {
                Retry(() => Directory.Delete(directory, recursive: true));
            }
        }
    }

    private void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < IoAttempts)
            {
                Thread.Sleep(_retryDelay);
            }
        }
    }
}
