// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamZH.Host.Logging;

/// <summary>Minimum level for the log file (or the console), changeable at runtime from the settings page.</summary>
public sealed class LogLevelSwitch(LogLevel minimum)
{
    public LogLevel Minimum { get; set; } = minimum;
}

/// <summary>Minimum level for the console output (Settings → General → Logging, advanced).</summary>
public sealed class ConsoleLogLevelSwitch(LogLevel minimum)
{
    public LogLevel Minimum { get; set; } = minimum;
}

/// <summary>Rotation of the log file: size of one file and how many old files are kept. Changeable at runtime.</summary>
public sealed class LogFileOptions
{
    public const int DefaultSizeLimitMb = 1;
    public const int DefaultFileCount = 5;
    public const int MaxSizeLimitMb = 100;
    public const int MaxFileCount = 50;

    public int SizeLimitMb { get; set; } = DefaultSizeLimitMb;

    /// <summary>Old files kept next to the current one (streamzh.1.txt … streamzh.N.txt).</summary>
    public int FileCount { get; set; } = DefaultFileCount;

    public long MaxBytes => SizeLimitMb * 1024L * 1024L;
}

/// <summary>One file in the log folder.</summary>
public sealed record LogFileInfo(string Name, long Size, DateTimeOffset LastWrite, bool Current);

/// <summary>One log entry: the first line split into its fields, the following lines (exception) as details.</summary>
public sealed record LogEntry(string Time, string Level, string Logger, string Message, string? Details);

/// <summary>
/// Writes logs to &lt;data&gt;/logs/streamzh.txt, rolls over at the size limit (default 1 MB) and keeps the configured
/// number of old files (default 5). Deliberately small and dependency-free.
/// </summary>
public sealed partial class FileLoggerProvider : ILoggerProvider
{
    public const string FileName = "streamzh.txt";

    /// <summary>File levels from least to most important; the names written into the file.</summary>
    public static readonly string[] FileLevels = ["Trace", "Debug", "Info", "Warn", "Error", "Fatal"];

    private readonly string _directory;
    private readonly LogLevelSwitch _switch;
    private readonly LogFileOptions _options;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;

    public FileLoggerProvider(string directory, LogLevelSwitch levelSwitch, LogFileOptions? options = null)
    {
        _directory = directory;
        _switch = levelSwitch;
        _options = options ?? new LogFileOptions();
        Directory.CreateDirectory(directory);
    }

    public string LogDirectory => _directory;

    public string CurrentFile => Path.Combine(_directory, FileName);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _switch.Minimum;

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append('|')
            .Append(LevelName(level)).Append('|')
            .Append(category).Append('|')
            .Append(message.ReplaceLineEndings(" "));

        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_lock)
        {
            try
            {
                _writer ??= Open();
                _writer.WriteLine(line);

                if (_writer.BaseStream.Length > _options.MaxBytes)
                {
                    Roll();
                }
            }
            catch (IOException)
            {
                // Logging must never take the app down (disk full, file locked by an editor, ...).
            }
        }
    }

    /// <summary>Returns the last lines of the current log file for the log viewer.</summary>
    public IReadOnlyList<string> Tail(int lines)
    {
        var buffer = new Queue<string>(lines);
        foreach (var line in ReadCurrentLines())
        {
            if (buffer.Count == lines)
            {
                buffer.Dequeue();
            }

            buffer.Enqueue(line);
        }

        return [.. buffer];
    }

    /// <summary>
    /// The newest entries of the current file, newest first, at least <paramref name="minimumLevel"/> (a file level name
    /// like "Warn") and containing <paramref name="search"/> (in message, logger or details, ignoring case).
    /// </summary>
    public IReadOnlyList<LogEntry> Entries(int count, string? minimumLevel = null, string? search = null)
    {
        var minimum = Array.FindIndex(FileLevels, l => string.Equals(l, minimumLevel, StringComparison.OrdinalIgnoreCase));
        var buffer = new Queue<LogEntry>(count);
        foreach (var entry in Parse(ReadCurrentLines()))
        {
            if (minimum > 0 && Array.IndexOf(FileLevels, entry.Level) < minimum)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(search)
                && !entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !entry.Logger.Contains(search, StringComparison.OrdinalIgnoreCase)
                && entry.Details?.Contains(search, StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            if (buffer.Count == count)
            {
                buffer.Dequeue();
            }

            buffer.Enqueue(entry);
        }

        return [.. buffer.Reverse()];
    }

    /// <summary>Groups lines into entries; lines without the time stamp (stack traces) belong to the entry before.</summary>
    public static IEnumerable<LogEntry> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        LogEntry? current = null;
        StringBuilder? details = null;
        foreach (var line in lines)
        {
            var match = EntryRegex().Match(line);
            if (match.Success)
            {
                if (current is not null)
                {
                    yield return current with { Details = details?.ToString() };
                }

                current = new LogEntry(match.Groups["time"].Value, match.Groups["level"].Value, match.Groups["logger"].Value, match.Groups["message"].Value, null);
                details = null;
            }
            else if (current is not null)
            {
                details = details is null ? new StringBuilder(line) : details.AppendLine().Append(line);
            }
        }

        if (current is not null)
        {
            yield return current with { Details = details?.ToString() };
        }
    }

    /// <summary>The log files, current first, then the old ones from newest to oldest.</summary>
    public IReadOnlyList<LogFileInfo> Files()
    {
        Flush();
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        return [.. new DirectoryInfo(_directory).EnumerateFiles("streamzh*.txt")
            .Where(f => IsLogFileName(f.Name))
            .Select(f => new LogFileInfo(f.Name, f.Length, f.LastWriteTimeUtc, f.Name == FileName))
            .OrderByDescending(f => f.Current)
            .ThenBy(f => FileNumber(f.Name))];
    }

    /// <summary>Full path of a log file, or null for names that are not log files (no paths, no other files).</summary>
    public string? PathOf(string name)
    {
        if (!IsLogFileName(name))
        {
            return null;
        }

        Flush();
        var path = Path.Combine(_directory, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Deletes one log file; the current one is emptied. False when it does not exist.</summary>
    public bool Delete(string name)
    {
        if (!IsLogFileName(name))
        {
            return false;
        }

        lock (_lock)
        {
            var path = Path.Combine(_directory, name);
            if (!File.Exists(path))
            {
                return false;
            }

            if (name == FileName)
            {
                _writer?.Dispose();
                _writer = null;
            }

            File.Delete(path);
            return true;
        }
    }

    public static bool IsLogFileName(string? name) => name is not null && LogFileNameRegex().IsMatch(name);

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Flush()
    {
        lock (_lock)
        {
            try
            {
                _writer?.Flush();
            }
            catch (IOException)
            {
            }
        }
    }

    private IEnumerable<string> ReadCurrentLines()
    {
        Flush();
        if (!File.Exists(CurrentFile))
        {
            yield break;
        }

        using var stream = new FileStream(CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private StreamWriter Open()
    {
        var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private void Roll()
    {
        _writer?.Dispose();
        _writer = null;

        var keep = Math.Max(1, _options.FileCount);

        // A lowered file count removes the files beyond it.
        foreach (var file in Directory.EnumerateFiles(_directory, "streamzh.*.txt"))
        {
            if (FileNumber(Path.GetFileName(file)) is var number and > 0 && number > keep)
            {
                File.Delete(file);
            }
        }

        for (var i = keep - 1; i >= 1; i--)
        {
            var source = Path.Combine(_directory, $"streamzh.{i}.txt");
            if (File.Exists(source))
            {
                File.Move(source, Path.Combine(_directory, $"streamzh.{i + 1}.txt"), overwrite: true);
            }
        }

        File.Move(CurrentFile, Path.Combine(_directory, "streamzh.1.txt"), overwrite: true);
    }

    /// <summary>0 for streamzh.txt, n for streamzh.n.txt.</summary>
    private static int FileNumber(string name)
    {
        var match = LogFileNameRegex().Match(name);
        return match.Success && match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private static string ShortCategory(string category)
    {
        var index = category.LastIndexOf('.');
        return index >= 0 && index < category.Length - 1 ? category[(index + 1)..] : category;
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Info",
        LogLevel.Warning => "Warn",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Fatal",
        _ => level.ToString(),
    };

    [GeneratedRegex(@"^streamzh(?:\.([0-9]{1,3}))?\.txt$")]
    private static partial Regex LogFileNameRegex();

    [GeneratedRegex(@"^(?<time>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\|(?<level>[A-Za-z]+)\|(?<logger>[^|]*)\|(?<message>.*)$")]
    private static partial Regex EntryRegex();

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
