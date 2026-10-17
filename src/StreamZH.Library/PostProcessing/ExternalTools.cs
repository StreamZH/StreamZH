// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Library.PostProcessing;

/// <summary>Turns par2 console output ("Loading: …", "Scanning: …", "Repairing: 45.6%") into one 0..1 progress.</summary>
public sealed class Par2RepairProgress
{
    private Phase _phase = Phase.Loading;

    private enum Phase
    {
        Loading,
        Scanning,
        Repairing,
        VerifyingRepaired,
    }

    /// <summary>Returns the overall progress for a line, or null when the line does not move it.</summary>
    public double? Update(string segment)
    {
        var text = segment.Trim();
        if (text.StartsWith("Verifying source files", StringComparison.Ordinal))
        {
            _phase = Phase.Scanning;
        }
        else if (text.StartsWith("Verifying repaired files", StringComparison.Ordinal))
        {
            _phase = Phase.VerifyingRepaired;
        }
        else if (text.StartsWith("Repairing:", StringComparison.Ordinal))
        {
            _phase = Phase.Repairing;
        }

        if (ToolOutput.Percent(text) is not { } percent)
        {
            return null;
        }

        // Loading 0-5 %, scanning 5-15 %, repairing 15-90 %, checking the result 90-100 %.
        return _phase switch
        {
            Phase.Loading => 0.05 * percent,
            Phase.Scanning => 0.05 + (0.10 * percent),
            Phase.Repairing => 0.15 + (0.75 * percent),
            _ => 0.90 + (0.10 * percent),
        };
    }
}

public sealed record ToolRunResult(bool Success, string? Error);

/// <summary>Calls par2cmdline (or par2cmdline-turbo) to repair a damaged set.</summary>
public sealed class Par2Repairer(IToolProcessRunner runner)
{
    /// <param name="mainPar2">The .par2 index file (or any file of the set when the index is missing).</param>
    /// <param name="extraPar2">The other .par2 files of the set; passed explicitly so obfuscated names are found too.</param>
    public async Task<ToolRunResult> RepairAsync(
        string par2Executable,
        string folder,
        string mainPar2,
        IEnumerable<string> extraPar2,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var parser = new Par2RepairProgress();
        var arguments = new List<string> { "r", mainPar2 };
        arguments.AddRange(extraPar2.Where(p => p != mainPar2));

        var result = await runner.RunAsync(par2Executable, arguments, folder, segment =>
        {
            if (parser.Update(segment) is { } value)
            {
                progress.Report(value);
            }
        }, timeout: null, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            progress.Report(1);
            return new ToolRunResult(true, null);
        }

        var reason = result.ExitCode switch
        {
            2 => "not enough recovery blocks",
            4 => "the par2 files are damaged",
            5 => "repair failed",
            6 => "file error",
            8 => "out of memory",
            _ => LastLine(result.Output),
        };
        return new ToolRunResult(false, $"par2 repair failed ({reason}, exit code {result.ExitCode})");
    }

    internal static string LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no output";
}

public enum ExtractFailure
{
    None,
    PasswordMissing,
    WrongPassword,
    Other,
}

public sealed record ExtractResult(bool Success, ExtractFailure Failure, string? Error);

/// <summary>Calls 7-Zip to extract one archive set (rar, 7z, zip, split files).</summary>
public sealed class SevenZipExtractor(IToolProcessRunner runner)
{
    /// <param name="password">Null: archives must not be encrypted (7-Zip gets "-" so it never asks).</param>
    public async Task<ExtractResult> ExtractAsync(
        string sevenZipExecutable,
        string firstVolume,
        string targetFolder,
        string? password,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetFolder);
        string[] arguments =
        [
            "x",
            "-y",
            "-bsp1",
            "-bb0",
            "-p" + (string.IsNullOrEmpty(password) ? "-" : password),
            "-o" + targetFolder,
            firstVolume,
        ];

        var result = await runner.RunAsync(sevenZipExecutable, arguments, Path.GetDirectoryName(firstVolume), segment =>
        {
            if (char.IsDigit(segment.TrimStart().FirstOrDefault()) && ToolOutput.Percent(segment) is { } value)
            {
                progress.Report(value);
            }
        }, timeout: null, cancellationToken).ConfigureAwait(false);

        return Interpret(result, password);
    }

    /// <summary>Exit code 0 is fine, 1 is a warning; password problems are recognised in the output.</summary>
    public static ExtractResult Interpret(ToolProcessResult result, string? password)
    {
        var output = result.Output;
        var passwordProblem = output.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Cannot open encrypted archive", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Can not open encrypted archive", StringComparison.OrdinalIgnoreCase);

        if (passwordProblem)
        {
            return string.IsNullOrEmpty(password)
                ? new ExtractResult(false, ExtractFailure.PasswordMissing, "Password protected: the archive needs a password. Add it to the title as {{password}} and retry.")
                : new ExtractResult(false, ExtractFailure.WrongPassword, "Password protected: the password is wrong.");
        }

        if (result.ExitCode is 0 or 1 && !output.Contains("ERROR:", StringComparison.Ordinal))
        {
            return new ExtractResult(true, ExtractFailure.None, null);
        }

        return new ExtractResult(false, ExtractFailure.Other, $"Extraction failed: {FirstError(output)} (exit code {result.ExitCode})");
    }

    /// <summary>
    /// The most useful error line. 7-Zip writes either "ERROR: Data Error : movie.mkv" or "ERROR: movie.7z.001" followed by
    /// the reason on the next line.
    /// </summary>
    private static string FirstError(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("ERROR:", StringComparison.Ordinal))
            {
                continue;
            }

            var text = lines[i]["ERROR:".Length..].Trim();
            var next = i + 1 < lines.Length ? lines[i + 1] : null;
            var nameOnly = !text.Contains(' ') || File.Exists(text);
            if (nameOnly && next is not null && !next.StartsWith("ERROR", StringComparison.Ordinal))
            {
                return $"{next} ({text})";
            }

            if (text.Length > 0)
            {
                return text;
            }
        }

        return lines.FirstOrDefault(l => l.Contains("Missing volume", StringComparison.OrdinalIgnoreCase)
                || l.Contains("Unexpected end", StringComparison.OrdinalIgnoreCase) || l.Contains("Can't open as archive", StringComparison.OrdinalIgnoreCase))
            ?? Par2Repairer.LastLine(output);
    }
}
