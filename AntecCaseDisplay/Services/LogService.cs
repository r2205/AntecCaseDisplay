namespace AntecCaseDisplay.Services;

/// <summary>
/// Tiny append-only log. Thread-safe; rotates at 5 MB by renaming to .1.
/// </summary>
public sealed class LogService
{
    private const long MaxBytes = 5L * 1024 * 1024;
    private readonly object _lock = new();
    private string? _path;

    public void Configure(bool enabled, string? path)
    {
        lock (_lock)
        {
            _path = enabled && !string.IsNullOrWhiteSpace(path)
                ? Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path)
                : null;
        }
    }

    public void Write(string line)
    {
        string? path;
        lock (_lock) { path = _path; }
        if (path is null) return;

        try
        {
            lock (_lock)
            {
                Rotate(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the worker.
        }
    }

    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            var rolled = path + ".1";
            if (File.Exists(rolled)) File.Delete(rolled);
            File.Move(path, rolled);
        }
        catch
        {
            // If rotation fails we'll just keep appending; not worth crashing over.
        }
    }
}
