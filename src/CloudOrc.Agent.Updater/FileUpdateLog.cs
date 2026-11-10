namespace CloudOrc.Agent.Updater;

/// <summary>Plain-text, timestamped log file (also echoed to the console). Thread-safe.</summary>
public sealed class FileUpdateLog : IUpdateLog
{
    private readonly string? _path;
    private readonly Lock _sync = new();

    public FileUpdateLog(string? path)
    {
        _path = path;
        if (path is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _path = null;
            }
        }
    }

    public void Info(string message) => Write("INF", message);
    public void Warn(string message) => Write("WRN", message);
    public void Error(string message) => Write("ERR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        lock (_sync)
        {
            Console.WriteLine(line);
            if (_path is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never break the update itself.
            }
        }
    }
}
