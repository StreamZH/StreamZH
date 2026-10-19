// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace StreamZH.Core;

/// <summary>
/// 0.12 (SV2 N-05): compares folders by what they really are, not by how their path is written. Windows: device paths
/// (\\?\…, \\.\…) and 8.3 short names (PROGRA~3) are refused in user input; the existing part of a path is resolved with
/// GetFinalPathNameByHandle (links, junctions, mapped and substituted drives) and compared case-insensitively. Linux and
/// macOS: realpath(3) of the existing part, plus device and inode numbers to recognise a folder under another name (a
/// bind mount); macOS names are compared in Unicode NFC and case-insensitively.
/// </summary>
public static partial class PathIdentity
{
    /// <summary>How paths are compared on this system (Linux: case-sensitive).</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Why <paramref name="path"/> may not be used as a folder someone typed or picked, null when it may: "." and ".."
    /// parts, control characters, and on Windows device paths (\\?\, \\.\, \??\), alternate data streams and 8.3 short
    /// names. Relative paths are not judged here.
    /// </summary>
    public static string? SyntaxProblem(string? path, bool windows)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var text = path.Trim();
        if (text.Any(c => c < 32 || c == 127))
        {
            return "The path contains control characters.";
        }

        if (windows)
        {
            var slashes = text.Replace('/', '\\');
            if (slashes.StartsWith(@"\\?\", StringComparison.Ordinal) || slashes.StartsWith(@"\\.\", StringComparison.Ordinal)
                || slashes.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                return @"Device paths (\\?\… or \\.\…) are not supported. Use a normal path such as D:\Media or \\server\share.";
            }

            // "C:" at the start is the drive; any other colon names an alternate data stream.
            var afterDrive = text.Length >= 2 && text[1] == ':' ? text[2..] : text;
            if (afterDrive.Contains(':', StringComparison.Ordinal))
            {
                return "The path contains a colon.";
            }
        }

        var root = windows ? WindowsRootLength(text) : (text.StartsWith('/') ? 1 : 0);
        var segments = text[root..].Split(windows ? ['\\', '/'] : ['/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".."))
        {
            return "Use a path without \".\" or \"..\" parts.";
        }

        if (windows && segments.Any(IsShortName))
        {
            return "Short names (such as PROGRA~1) are not supported. Use the full folder names.";
        }

        return null;
    }

    /// <summary>An 8.3 short name: at most 8 characters before the extension, ending in "~" and digits ("PROGRA~3", "MYDOCU~1.TXT").</summary>
    public static bool IsShortName(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return ShortNameRegex().IsMatch(segment);
    }

    [GeneratedRegex(@"^[^.~\s]{1,6}~[0-9]{1,6}(\.[^.]{1,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortNameRegex();

    private static int WindowsRootLength(string text)
    {
        if (text.Length >= 2 && text[1] == ':')
        {
            return text.Length >= 3 && text[2] is '\\' or '/' ? 3 : 2;
        }

        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal))
        {
            // \\server\share\
            var parts = text[2..].Split(['\\', '/'], 3);
            return parts.Length >= 2 ? Math.Min(text.Length, 2 + parts[0].Length + 1 + parts[1].Length + 1) : text.Length;
        }

        return text.StartsWith('\\') || text.StartsWith('/') ? 1 : 0;
    }

    /// <summary>The full path without a trailing separator, in NFC on macOS (for comparisons, not for storing).</summary>
    public static string Normalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        return OperatingSystem.IsMacOS() ? full.Normalize(NormalizationForm.FormC) : full;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or below it (normalized path text).</summary>
    public static bool IsSameOrInside(string path, string folder) => DataFolderGuard.IsSameOrInside(path, folder);

    /// <summary>
    /// The path as the system resolves it: the longest existing part resolved (Windows: GetFinalPathNameByHandle, else
    /// realpath(3)), the rest appended unchanged. Falls back to following links segment by segment when the system call is
    /// not available, and to the path itself when nothing exists.
    /// </summary>
    public static string Real(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        var full = Normalize(fullPath);
        var existing = full;
        var tail = new Stack<string>();
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || parent == existing)
            {
                return DataFolderGuard.RealPath(full);
            }

            tail.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = OperatingSystem.IsWindows() ? FinalPathWindows(existing) : RealPathUnix(existing);
        if (resolved is null)
        {
            return DataFolderGuard.RealPath(full);
        }

        var result = tail.Count == 0 ? resolved : Path.Combine([resolved, .. tail]);
        result = Path.TrimEndingDirectorySeparator(result);
        return OperatingSystem.IsMacOS() ? result.Normalize(NormalizationForm.FormC) : result;
    }

    /// <summary>Device and inode of an existing file or folder (Linux, macOS); null elsewhere or when it cannot be read.</summary>
    public static (ulong Device, ulong Inode)? Identity(string path)
    {
        if (OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            return null;
        }

        var buffer = new byte[512];
        try
        {
            int result;
            if (OperatingSystem.IsMacOS())
            {
                result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? StatInode64(path, buffer) : Stat(path, buffer);
                if (result != 0)
                {
                    return null;
                }

                // struct stat (64-bit inodes): dev_t st_dev (int32) at 0, ino_t st_ino (uint64) at 8.
                return ((ulong)BitConverter.ToUInt32(buffer, 0), BitConverter.ToUInt64(buffer, 8));
            }

            try
            {
                result = Stat(path, buffer);
            }
            catch (EntryPointNotFoundException)
            {
                // glibc before 2.33 exports only __xstat (version 1 on x86-64, 0 on arm64).
                result = XStat(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 1 : 0, path, buffer);
            }

            // Linux x86-64 and arm64: dev_t st_dev (uint64) at 0, ino_t st_ino (uint64) at 8.
            return result == 0 ? (BitConverter.ToUInt64(buffer, 0), BitConverter.ToUInt64(buffer, 8)) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
    }

    /// <summary>The existing folders from <paramref name="fullPath"/> up to the root (deepest first).</summary>
    public static IEnumerable<string> ExistingAncestors(string fullPath)
    {
        var current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
            {
                yield return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                yield break;
            }

            current = parent;
        }
    }

    private static string? RealPathUnix(string existing)
    {
        try
        {
            var pointer = RealPathNative(existing, IntPtr.Zero);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUTF8(pointer);
            }
            finally
            {
                Free(pointer);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? FinalPathWindows(string existing)
    {
        const uint shareAll = 0x1 | 0x2 | 0x4;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        using var handle = CreateFile(existing, 0, shareAll, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[1024];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        }

        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        var final = new string(buffer, 0, (int)length);
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + final[8..];
        }

        return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

#pragma warning disable SYSLIB1054, CA2101 // Plain DllImport, like ImportFileSystem: no unsafe code needed.
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPathNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "__xstat", SetLastError = true)]
    private static extern int XStat(int version, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int StatInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);
#pragma warning restore SYSLIB1054, CA2101
}
