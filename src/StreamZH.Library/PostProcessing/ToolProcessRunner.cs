// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Text;

namespace StreamZH.Library.PostProcessing;

/// <param name="Output">Standard output and error, without the progress noise (only the last part for long runs).</param>
public sealed record ToolProcessResult(int ExitCode, string Output);

/// <summary>Runs par2 and 7-Zip as separate processes (they are not linked into StreamZH).</summary>
public interface IToolProcessRunner
{
    /// <param name="onOutput">Called for each piece of output; pieces are split at line breaks, carriage returns and backspaces, so progress counters arrive one by one.</param>
    /// <exception cref="OperationCanceledException">The process was stopped because of <paramref name="cancellationToken"/>.</exception>
    Task<ToolProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        Action<string>? onOutput,
        TimeSpan? timeout,
        CancellationToken cancellationToken);
}

/// <summary>Runs the tools with the process priority from the settings (normal, below normal or idle).</summary>
public sealed class ToolProcessRunner(IPostProcessSwitches? switches = null) : IToolProcessRunner
{
    private const int MaxKeptOutput = 64 * 1024;

    /// <summary>Lowers the priority of a started process; "nice" 10 or 19 on Linux and macOS. Never throws.</summary>
    public static void ApplyPriority(Process process, Downloads.ToolPriority priority)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (priority == Downloads.ToolPriority.Normal)
        {
            return;
        }

        try
        {
            process.PriorityClass = priority == Downloads.ToolPriority.Idle ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
        {
            // Exited already, or the system does not allow it: the tool runs at normal priority.
        }
    }

    public async Task<ToolProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        Action<string>? onOutput,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        process.Start();
        ApplyPriority(process, switches?.PostProcess.ToolPriority ?? Downloads.ToolPriority.Normal);

        // Nothing is ever typed in: a tool that asks (e.g. for a password) gets end of input instead of hanging.
        process.StandardInput.Close();

        var kept = new KeptOutput();
        var sync = new object();
        void Emit(string segment)
        {
            lock (sync)
            {
                kept.Add(segment);
                onOutput?.Invoke(segment);
            }
        }

        using var timeoutSource = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        await using var registration = linked.Token.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        var stdout = PumpAsync(process.StandardOutput, Emit);
        var stderr = PumpAsync(process.StandardError, Emit);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within {timeout!.Value.TotalSeconds:0} s.");
        }

        return new ToolProcessResult(process.ExitCode, kept.ToString());
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> emit)
    {
        var buffer = new char[4096];
        var current = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\n' or '\r' or '\b')
                {
                    Flush();
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        Flush();

        void Flush()
        {
            var text = current.ToString().Trim();
            current.Clear();
            if (text.Length > 0)
            {
                emit(text);
            }
        }
    }

    /// <summary>Keeps the meaningful lines (no bare progress counters) and only the last part of a long output.</summary>
    private sealed class KeptOutput
    {
        private readonly StringBuilder _text = new();

        public void Add(string segment)
        {
            if (ToolOutput.IsProgressNoise(segment))
            {
                return;
            }

            _text.AppendLine(segment);
            if (_text.Length > MaxKeptOutput * 2)
            {
                _text.Remove(0, _text.Length - MaxKeptOutput);
            }
        }

        public override string ToString() => _text.ToString();
    }
}

/// <summary>Helpers for reading par2 and 7-Zip console output.</summary>
public static class ToolOutput
{
    /// <summary>"Repairing: 45.6%", "  12% 3 - file.rar", "Scanning: 3.0%" and similar counters.</summary>
    public static bool IsProgressNoise(string segment)
    {
        var text = segment.Trim();
        return text.Length == 0
            || (text.Contains('%') && (text.StartsWith("Loading:", StringComparison.Ordinal) || text.StartsWith("Scanning:", StringComparison.Ordinal)
                || text.StartsWith("Repairing:", StringComparison.Ordinal) || text.StartsWith("Constructing:", StringComparison.Ordinal)
                || char.IsDigit(text[0])));
    }

    /// <summary>The first percentage in the text as 0..1, or null.</summary>
    public static double? Percent(string segment)
    {
        var index = segment.IndexOf('%');
        if (index <= 0)
        {
            return null;
        }

        var start = index;
        while (start > 0 && (char.IsDigit(segment[start - 1]) || segment[start - 1] == '.'))
        {
            start--;
        }

        return start < index && double.TryParse(segment.AsSpan(start, index - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value / 100, 0, 1)
            : null;
    }
}
