using CloudOrc.Agent.Updater;

namespace CloudOrc.Agent.Updater.Tests;

public sealed class DirectoryBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "co-upd-backup-" + Guid.NewGuid().ToString("N"));
    private readonly DirectoryBackup _backup = new(TimeSpan.FromMilliseconds(1));

    public DirectoryBackupTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Install => Path.Combine(_root, "install");
    private string Backup => Path.Combine(_root, "work", "rollback", "u1");

    private void Write(string relative, string content)
    {
        var path = Path.Combine(Install, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Backup_copies_tree_recursively()
    {
        Write(@"ControlAgent\agent.dll", "v1");
        Write(@"ControlAgent\sub\deep.txt", "deep");
        Directory.CreateDirectory(Path.Combine(Install, "EmptyDir"));

        _backup.Backup(Install, Backup);

        Assert.Equal("v1", File.ReadAllText(Path.Combine(Backup, @"ControlAgent\agent.dll")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(Backup, @"ControlAgent\sub\deep.txt")));
        Assert.True(Directory.Exists(Path.Combine(Backup, "EmptyDir")));
    }

    [Fact]
    public void Restore_overwrites_changed_files_and_deletes_extra_files_and_dirs()
    {
        Write(@"ControlAgent\agent.dll", "v1");
        Write(@"WatchdogAgent\watchdog.dll", "w1");
        _backup.Backup(Install, Backup);

        // Simulate a failed install: changed file, deleted file, new file, new nested dir.
        Write(@"ControlAgent\agent.dll", "v2");
        File.Delete(Path.Combine(Install, @"WatchdogAgent\watchdog.dll"));
        Write(@"ControlAgent\new-only.dll", "new");
        Write(@"NewFolder\nested\file.txt", "x");

        _backup.Restore(Backup, Install);

        Assert.Equal("v1", File.ReadAllText(Path.Combine(Install, @"ControlAgent\agent.dll")));
        Assert.Equal("w1", File.ReadAllText(Path.Combine(Install, @"WatchdogAgent\watchdog.dll")));
        Assert.False(File.Exists(Path.Combine(Install, @"ControlAgent\new-only.dll")));
        Assert.False(Directory.Exists(Path.Combine(Install, "NewFolder")));
    }

    [Fact]
    public void Restore_removes_read_only_extra_files()
    {
        Write(@"ControlAgent\agent.dll", "v1");
        _backup.Backup(Install, Backup);
        Write(@"ControlAgent\readonly.dll", "ro");
        File.SetAttributes(Path.Combine(Install, @"ControlAgent\readonly.dll"), FileAttributes.ReadOnly);

        _backup.Restore(Backup, Install);

        Assert.False(File.Exists(Path.Combine(Install, @"ControlAgent\readonly.dll")));
    }

    [Fact]
    public void Backup_of_missing_install_dir_throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _backup.Backup(Path.Combine(_root, "missing"), Backup));
    }

    [Fact]
    public void Backup_replaces_stale_backup_contents()
    {
        Write(@"a.txt", "a");
        _backup.Backup(Install, Backup);
        File.Delete(Path.Combine(Install, "a.txt"));
        Write(@"b.txt", "b");

        _backup.Backup(Install, Backup);

        Assert.False(File.Exists(Path.Combine(Backup, "a.txt")));
        Assert.True(File.Exists(Path.Combine(Backup, "b.txt")));
    }

    [Fact]
    public void DeleteBackup_removes_directory()
    {
        Write(@"a.txt", "a");
        _backup.Backup(Install, Backup);

        _backup.DeleteBackup(Backup);

        Assert.False(Directory.Exists(Backup));
    }
}
