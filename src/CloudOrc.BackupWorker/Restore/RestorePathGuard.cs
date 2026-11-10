using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.BackupWorker.Restore;

public sealed record RestorePlan(string TargetDirectory, IReadOnlyList<string?> Items);

/// <summary>
/// §10: "Protect backup catalog and file explorer against cross-tenant enumeration, path
/// traversal, symlink escape, unsafe preview and unauthorized direct download. Restore is
/// separately authorized; overwrite or destructive restore requires impact preview, confirmation
/// and approval." The backend handles authorization/approval; this guard makes sure that, even
/// with a valid approved command, nothing can be written outside the intended target on disk.
/// </summary>
public static class RestorePathGuard
{
    private static readonly string[] ForbiddenRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
    ];

    public static RestorePlan Plan(RestoreExecuteParameters restore, DateTimeOffset now)
    {
        var sourceRoot = Path.GetFullPath(restore.SourceRoot ?? throw new RestoreRejectedException("sourceRoot is missing."));
        var baseTarget = restore.TargetKind == RestoreTargetKinds.Original
            ? sourceRoot
            : Path.GetFullPath(restore.TargetPath ?? throw new RestoreRejectedException("targetPath is missing."));

        // An alternate target may never be a system location; restoring to the original
        // location is allowed wherever the backup was taken from (the policy chose that path).
        if (restore.TargetKind == RestoreTargetKinds.Alternate && IsUnderForbiddenRoot(baseTarget))
        {
            throw new RestoreRejectedException("Restoring into Windows, Program Files or ProgramData is not allowed.");
        }

        // "rename" never touches existing data: restore into a new sibling folder.
        var target = restore.Mode == RestoreModes.Rename
            ? restore.TargetKind == RestoreTargetKinds.Original
                ? $"{baseTarget.TrimEnd('\\')}.restored-{now:yyyyMMdd-HHmmss}"
                : Path.Combine(baseTarget, $"restored-{now:yyyyMMdd-HHmmss}")
            : baseTarget;

        EnsureNoReparsePointOnPath(target);

        var items = new List<string?>();
        if (restore.Selection.Count == 0)
        {
            items.Add(null);
        }
        else
        {
            foreach (var raw in restore.Selection)
            {
                var relative = NormalizeRelative(raw);
                var destination = Path.GetFullPath(Path.Combine(target, relative));
                if (!IsInside(destination, target))
                {
                    throw new RestoreRejectedException($"Selected path escapes the restore target: {Trim(raw)}");
                }

                EnsureNoReparsePointOnPath(destination);
                items.Add(relative);
            }
        }

        // Overwrite/skip-existing write into EXISTING folders: a junction planted inside them (e.g.
        // pointing at C:\Windows) would let the SYSTEM-run engine write outside the target.
        if (restore.Mode != RestoreModes.Rename)
        {
            foreach (var item in items)
            {
                var destination = item is null ? target : Path.Combine(target, item);
                if (Directory.Exists(destination) && FindReparsePoint(destination) is { } link)
                {
                    throw new RestoreRejectedException(
                        $"The restore target contains a link or junction ({Trim(link)}); restore to a new folder (rename) or remove the link first.");
                }
            }
        }

        return new RestorePlan(target, items);
    }

    public static string NormalizeRelative(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is ".." or "." || p.Contains(':', StringComparison.Ordinal)))
        {
            throw new RestoreRejectedException($"Unsafe selected path: {Trim(path)}");
        }

        return string.Join('\\', parts);
    }

    public static bool IsInside(string candidate, string root)
    {
        var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var c = Path.GetFullPath(candidate);
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase) || string.Equals(c.TrimEnd('\\'), r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every EXISTING component of the path must be a real directory, never a symlink/junction/mount point.</summary>
    public static void EnsureNoReparsePointOnPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                return; // the rest will be created by the restore
            }

            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new RestoreRejectedException($"The restore path contains a link or junction ({Trim(current)}); refusing to follow it.");
            }
        }
    }

    /// <summary>Post-restore audit: counts what was written and flags any link that points outside the target.</summary>
    public static (long Files, long Bytes, int UnsafeLinks) Audit(string target)
    {
        long files = 0, bytes = 0;
        var unsafeLinks = 0;
        if (!Directory.Exists(target))
        {
            return (0, 0, 0);
        }

        foreach (var entry in new DirectoryInfo(target).EnumerateFileSystemInfos("*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                     AttributesToSkip = 0
                 }))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var linkTarget = entry.LinkTarget;
                if (linkTarget is null || !IsInside(Path.IsPathRooted(linkTarget) ? linkTarget : Path.Combine(Path.GetDirectoryName(entry.FullName)!, linkTarget), target))
                {
                    unsafeLinks++;
                }

                continue;
            }

            if (entry is FileInfo f)
            {
                files++;
                bytes += f.Length;
            }
        }

        return (files, bytes, unsafeLinks);
    }

    /// <summary>First reparse point (symlink/junction/mount point) inside <paramref name="root"/>, without following any.</summary>
    public static string? FindReparsePoint(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 });
                foreach (var entry in entries)
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        return entry.FullName;
                    }

                    if (entry is DirectoryInfo sub)
                    {
                        pending.Push(sub.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    private static bool IsUnderForbiddenRoot(string path) =>
        ForbiddenRoots.Where(r => !string.IsNullOrEmpty(r)).Any(r => IsInside(path, r));

    private static string Trim(string value) => value.Length > 80 ? value[..80] + "..." : value;
}

public sealed class RestoreRejectedException(string message) : Exception(message);
