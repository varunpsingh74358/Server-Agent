using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.BackupWorker.Scanning;

public sealed record ScanResult(long Files, long Directories, long Bytes, long Errors, long ManifestEntries, bool ManifestTruncated, long DurationMs, IReadOnlyList<string> MissingSources);

/// <summary>
/// Walks the sources once per job to (1) size the work (admission estimate for the next run,
/// progress totals) and (2) write the recovery-point manifest (gzip NDJSON) the portal File
/// Explorer browses (§5 "Recovery Point File Explorer", §11 recovery_point.manifest_ref), so
/// browsing never needs the endpoint online and never touches the repository.
///
/// Bounded (§6): streaming writer, entry cap, never follows reparse points (no symlink/junction
/// escape, no loops), honours the policy's exclusions, stops on cancellation.
/// Paths in the manifest are RELATIVE to their source root - no absolute paths leave the device.
/// </summary>
public static class SourceScanner
{
    public static ScanResult Scan(
        IReadOnlyList<BackupSource> sources,
        string? manifestPath,
        int maxManifestEntries,
        Action<long, long>? onProgress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long files = 0, dirs = 0, bytes = 0, errors = 0, entries = 0;
        var truncated = false;
        var missing = new List<string>();

        Stream? output = null;
        StreamWriter? writer = null;
        if (manifestPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            output = new GZipStream(new FileStream(manifestPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16), CompressionLevel.Fastest);
            writer = new StreamWriter(output, new UTF8Encoding(false), 1 << 16);
        }

        try
        {
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = Path.GetFullPath(source.Path);
                var excludes = new GlobMatcher(source.Excludes);

                if (File.Exists(root))
                {
                    var info = new FileInfo(root);
                    files++;
                    bytes += info.Length;
                    Write(source.Path, info.Name, "file", info.Length, info.LastWriteTimeUtc);
                    continue;
                }

                if (!Directory.Exists(root))
                {
                    missing.Add(source.Path);
                    continue;
                }

                var stack = new Stack<DirectoryInfo>();
                stack.Push(new DirectoryInfo(root));
                while (stack.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dir = stack.Pop();
                    IEnumerable<FileSystemInfo> children;
                    try
                    {
                        children = dir.EnumerateFileSystemInfos("*", new EnumerationOptions
                        {
                            IgnoreInaccessible = false,
                            RecurseSubdirectories = false,
                            AttributesToSkip = 0,
                            ReturnSpecialDirectories = false
                        }).ToList();
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        errors++;
                        continue;
                    }

                    foreach (var child in children)
                    {
                        var relative = Path.GetRelativePath(root, child.FullName).Replace('\\', '/');
                        if (excludes.IsMatch(relative))
                        {
                            continue;
                        }

                        // Never follow symlinks/junctions/mount points out of the source.
                        if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            Write(source.Path, relative, "link", 0, child.LastWriteTimeUtc);
                            continue;
                        }

                        if (child is DirectoryInfo d)
                        {
                            dirs++;
                            Write(source.Path, relative, "dir", 0, d.LastWriteTimeUtc);
                            stack.Push(d);
                        }
                        else if (child is FileInfo f)
                        {
                            files++;
                            var length = SafeLength(f, ref errors);
                            bytes += length;
                            Write(source.Path, relative, "file", length, f.LastWriteTimeUtc);
                            if (files % 5000 == 0)
                            {
                                onProgress?.Invoke(files, bytes);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            writer?.Flush();
            writer?.Dispose();
            output?.Dispose();
        }

        if (manifestPath is not null)
        {
            File.Move(manifestPath + ".tmp", manifestPath, overwrite: true);
        }

        onProgress?.Invoke(files, bytes);
        return new ScanResult(files, dirs, bytes, errors, entries, truncated, stopwatch.ElapsedMilliseconds, missing);

        void Write(string rootPath, string relative, string type, long size, DateTime mtime)
        {
            if (writer is null)
            {
                return;
            }

            if (entries >= maxManifestEntries)
            {
                truncated = true;
                return;
            }

            entries++;
            writer.WriteLine(JsonSerializer.Serialize(new ManifestEntry
            {
                Root = rootPath,
                Path = relative,
                Type = type,
                Size = size,
                ModifiedAt = new DateTimeOffset(DateTime.SpecifyKind(mtime, DateTimeKind.Utc))
            }, Agent.Contracts.Protocol.ProtocolJson.Options));
        }
    }

    private static long SafeLength(FileInfo file, ref long errors)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors++;
            return 0;
        }
    }
}

/// <summary>
/// Minimal glob matcher for policy exclusions, relative to the source root, case-insensitive:
/// <c>*</c> (within a segment), <c>?</c>, <c>**</c> (any depth). A pattern without a slash matches
/// a name at any depth (e.g. "*.tmp", "node_modules").
/// </summary>
public sealed class GlobMatcher
{
    private readonly List<System.Text.RegularExpressions.Regex> _patterns = [];

    public GlobMatcher(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var pattern = raw.Replace('\\', '/').Trim().Trim('/');
            if (pattern.Length == 0)
            {
                continue;
            }

            var anyDepth = !pattern.Contains('/', StringComparison.Ordinal);
            var regex = new StringBuilder(anyDepth ? "(^|.*/)" : "^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    regex.Append(".*");
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        i++;
                    }
                }
                else if (c == '*')
                {
                    regex.Append("[^/]*");
                }
                else if (c == '?')
                {
                    regex.Append("[^/]");
                }
                else
                {
                    regex.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
                }
            }

            regex.Append("(/.*)?$");
            _patterns.Add(new System.Text.RegularExpressions.Regex(regex.ToString(),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)));
        }
    }

    public bool IsMatch(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        foreach (var regex in _patterns)
        {
            try
            {
                if (regex.IsMatch(path))
                {
                    return true;
                }
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
            }
        }

        return false;
    }
}
