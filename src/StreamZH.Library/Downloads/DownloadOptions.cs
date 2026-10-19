// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamZH.Library.Downloads;

/// <summary>What happens with a download whose release is already in the queue or the history (SABnzbd "Detect duplicate downloads").</summary>
public enum DuplicateAction
{
    Off,

    /// <summary>Downloaded anyway, but marked as a duplicate.</summary>
    Tag,

    /// <summary>Added paused and marked.</summary>
    Pause,

    /// <summary>Not downloaded: goes straight to the history as failed.</summary>
    Fail,
}

/// <summary>How new downloads are sorted into the queue (within their priority), like SABnzbd's "Automatically sort queue".</summary>
public enum QueueSort
{
    Off,
    Oldest,
    Newest,
    Smallest,
    Largest,
    Name,
}

/// <summary>What happens when a download contains a file with an unwanted extension.</summary>
public enum UnwantedAction
{
    Off,

    /// <summary>Logged and shown on the download; nothing else.</summary>
    Warn,

    /// <summary>The download is paused (found in the NZB) or fails (found after unpacking).</summary>
    Pause,
    Fail,
}

/// <summary>What happens when the first RAR volume turns out to be password protected and no password is known.</summary>
public enum EncryptedAction
{
    /// <summary>Downloaded to the end; unpacking then fails for want of a password.</summary>
    Off,
    Pause,
    Fail,
}

/// <summary>Process priority of par2 and 7-Zip.</summary>
public enum ToolPriority
{
    Normal,
    BelowNormal,
    Idle,
}

/// <summary>Values of the settings enums as the API sends them (camelCase) and back.</summary>
public static class DownloadOptionValues
{
    public static string Of<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static T? Parse<T>(string? value)
        where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(value) && !char.IsDigit(value.Trim()[0]) && Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var parsed) ? parsed : null;

    public static string Names<T>()
        where T : struct, Enum => string.Join(", ", Enum.GetValues<T>().Select(Of));
}

/// <summary>Lists of file extensions as typed in the settings (".nfo, .sfv url").</summary>
public static partial class ExtensionList
{
    /// <summary>Lower-case extensions with a leading dot; null when one of them is not a plain extension.</summary>
    public static IReadOnlyList<string>? Parse(string? text, out string? invalid)
    {
        invalid = null;
        var result = new List<string>();
        foreach (var raw in (text ?? string.Empty).Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var extension = raw.Trim().ToLowerInvariant();
            extension = extension.StartsWith('*') ? extension[1..] : extension;
            extension = extension.StartsWith('.') ? extension : "." + extension;
            if (!ExtensionRegex().IsMatch(extension))
            {
                invalid = raw;
                return null;
            }

            if (!result.Contains(extension))
            {
                result.Add(extension);
            }
        }

        return result;
    }

    public static string Format(IEnumerable<string> extensions) => string.Join(", ", extensions);

    [GeneratedRegex(@"^\.[a-z0-9_-]{1,15}$")]
    private static partial Regex ExtensionRegex();
}

/// <summary>
/// Extra command-line parameters for par2 and 7-Zip. They are split into single arguments (quotes group words) and
/// passed as an argument list, never through a shell; each must look like an option ("-t4"), and options that would
/// change where files go or what password is used are refused. Since 0.12 only an allow-list of switches is accepted
/// (threads, memory, output detail, character sets): anything that could make the tools read or write elsewhere
/// (-spf, -i, -B, …) is out, also when it was stored by an earlier version (<see cref="IsAllowed"/>).
/// </summary>
public static partial class ToolArguments
{
    public const int MaxArguments = 10;

    /// <summary>What the help texts list as allowed.</summary>
    public const string Par2Allowed = "-t<threads>, -T<file threads>, -m<memory MB>, -N, -S<n>, -q, -qq, -v, -vv";

    public const string SevenZipAllowed = "-mmt, -mmt<threads>, -mmt=on|off|<threads>, -bb0..3, -bd, -bt, -bso/-bse/-bsp0..2, -scc<charset>, -mcp=<code page>, -ssc, -ssc-, -aoa, -aos, -aot, -aou";

    private static readonly string[] Par2Refused = ["-B"];

    private static readonly string[] SevenZipRefused = ["-o", "-p", "-si", "-so", "-sdel", "-ai", "-an", "-ax", "-i", "-x", "-t", "-v", "-w", "-y", "-r"];

    /// <summary>The arguments, or null with the reason.</summary>
    public static IReadOnlyList<string>? Parse(string? text, bool sevenZip, out string? problem)
    {
        problem = null;
        var arguments = Split(text ?? string.Empty);
        if (arguments.Count > MaxArguments)
        {
            problem = $"Use at most {MaxArguments.ToString(CultureInfo.InvariantCulture)} parameters.";
            return null;
        }

        foreach (var argument in arguments)
        {
            if (!OptionRegex().IsMatch(argument))
            {
                problem = $"\"{argument}\" is not an option. Use options like {(sevenZip ? "-mmt4" : "-t4")}.";
                return null;
            }

            var refused = (sevenZip ? SevenZipRefused : Par2Refused)
                .FirstOrDefault(r => argument.StartsWith(r, sevenZip ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (refused is not null)
            {
                problem = $"{refused} cannot be set here; StreamZH sets it itself.";
                return null;
            }

            if (!IsAllowed(argument, sevenZip))
            {
                problem = $"\"{argument}\" is not allowed here. Use {(sevenZip ? SevenZipAllowed : Par2Allowed)}.";
                return null;
            }
        }

        return arguments;
    }

    /// <summary>An argument from the allow-list of the tool (also used to drop disallowed values stored before 0.12).</summary>
    public static bool IsAllowed(string argument, bool sevenZip) =>
        argument is not null && (sevenZip ? SevenZipAllowedRegex() : Par2AllowedRegex()).IsMatch(argument);

    private static List<string> Split(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    [GeneratedRegex(@"^-{1,2}[A-Za-z0-9][A-Za-z0-9=._:+-]{0,40}$")]
    private static partial Regex OptionRegex();

    // par2cmdline (and -turbo) repair switches: threads, file threads, memory, no data skipping, skip leeway, quiet, verbose.
    [GeneratedRegex(@"^(-t[0-9]{1,3}|-T[0-9]{1,3}|-m[0-9]{1,6}|-N|-S[0-9]{1,6}|-qq?|-vv?)$", RegexOptions.CultureInvariant)]
    private static partial Regex Par2AllowedRegex();

    // 7-Zip extract switches: threads, output detail, file name character sets, case sensitivity, overwrite mode.
    [GeneratedRegex(
        @"^(-mmt|-mmt[0-9]{1,3}|-mmt=(on|off|[0-9]{1,3})|-bb[0-3]|-bd|-bt|-bs[oep][0-2]|-scc(utf-8|win|dos)|-mcp=[0-9]{1,5}|-ssc-?|-ao[asut])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SevenZipAllowedRegex();
}
