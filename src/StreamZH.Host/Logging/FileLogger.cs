// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;

namespace StreamZH.Host.Logging;

/// <summary>Minimum level for the log file, changeable at runtime from the settings page.</summary>
public sealed class LogLevelSwitch(LogLevel minimum)
{
    public LogLevel Minimum { get; set; } = minimum;
}

/// <summary>
/// Writes logs to &lt;data&gt;/logs/streamzh.txt, rolls over at 1 MB and keeps 5 old files.
/// Deliberately small and dependency-free.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const string FileName = "streamzh.txt";
    private const long MaxBytes = 1024 * 1024;
    private const int KeepFiles = 5;

    private readonly string _directory;
    private readonly LogLevelSwitch _switch;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;

    public FileLoggerProvider(string directory, LogLevelSwitch levelSwitch)
    {
        _directory = directory;
        _switch = levelSwitch;
        Directory.CreateDirectory(directory);
    }

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

                if (_writer.BaseStream.Length > MaxBytes)
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
        lock (_lock)
        {
            _writer?.Flush();
        }

        if (!File.Exists(CurrentFile))
        {
            return [];
        }

        using var stream = new FileStream(CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var buffer = new Queue<string>(lines);

        while (reader.ReadLine() is { } line)
        {
            if (buffer.Count == lines)
            {
                buffer.Dequeue();
            }

            buffer.Enqueue(line);
        }

        return [.. buffer];
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
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

        for (var i = KeepFiles - 1; i >= 1; i--)
        {
            var source = Path.Combine(_directory, $"streamzh.{i}.txt");
            if (File.Exists(source))
            {
                File.Move(source, Path.Combine(_directory, $"streamzh.{i + 1}.txt"), overwrite: true);
            }
        }

        File.Move(CurrentFile, Path.Combine(_directory, "streamzh.1.txt"), overwrite: true);
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