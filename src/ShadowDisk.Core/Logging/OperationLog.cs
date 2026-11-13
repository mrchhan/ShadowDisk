using System.Globalization;

namespace ShadowDisk.Core.Logging;

public enum LogLevel { Info, Warning, Error }

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Message)
{
    public override string ToString() =>
        $"{Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} [{Level}] {Message}";
}

/// <summary>
/// Timestamped log kept in memory and appended to a daily file. Only log what happened and its
/// result — never file names, folder contents or full serial numbers.
/// </summary>
public sealed class OperationLog
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];
    private readonly string? _directory;
    private readonly Func<DateTimeOffset> _clock;

    public OperationLog(string? directory, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public event Action<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public string? CurrentFilePath =>
        _directory is null ? null : Path.Combine(_directory, $"shadowdisk-{_clock():yyyy-MM-dd}.log");

    public void Info(string message) => Add(LogLevel.Info, message);
    public void Warning(string message) => Add(LogLevel.Warning, message);
    public void Error(string message) => Add(LogLevel.Error, message);

    private void Add(LogLevel level, string message)
    {
        var entry = new LogEntry(_clock(), level, message);
        lock (_gate)
        {
            _entries.Add(entry);
            if (CurrentFilePath is { } path)
            {
                try
                {
                    Directory.CreateDirectory(_directory!);
                    File.AppendAllText(path, entry + Environment.NewLine);
                }
                catch (IOException) { /* Logging must never break the app. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        EntryAdded?.Invoke(entry);
    }
}
