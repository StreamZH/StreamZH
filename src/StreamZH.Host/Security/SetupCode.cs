// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using StreamZH.Core;
using StreamZH.Data;
using StreamZH.Host.Tray;

namespace StreamZH.Host.Security;

/// <summary>
/// First-run setup protection (0.12, security review S1-14/S3-01). Until the owner account exists, StreamZH creates a
/// one-time setup code at start: printed to the console and the log (in Docker: <c>docker logs</c>), written to
/// &lt;data&gt;/setup-code (readable by StreamZH's own account only) and shown by the Windows tray icon. The setup page
/// needs it only when it is opened from another device: on this computer (loopback or its own address, no reverse
/// proxy in between) setup works without it, like before.
/// </summary>
public sealed class SetupCode(AppPaths paths, UserRepository users, ILogger<SetupCode> logger) : IHostedService
{
    public const string FileName = "setup-code";

    // No 0/O, 1/I/L: easy to read from a console and to type.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int Length = 12;

    private readonly Lock _lock = new();
    private string? _code;

    public string FilePath => Path.Combine(paths.DataDirectory, FileName);

    /// <summary>The code while setup is still to be done; null afterwards.</summary>
    public string? Current
    {
        get
        {
            lock (_lock)
            {
                return _code is not null && users.Count() == 0 ? _code : null;
            }
        }
    }

    /// <summary>
    /// This request needs the code: no owner yet, and not from this computer by one of its own names. A connection from
    /// this computer with another Host name (a web page that rebinds its own name to 127.0.0.1, 0.12 V-03) needs it too.
    /// </summary>
    public bool IsRequired(HttpContext context) =>
        users.Count() == 0 && !(TrayAuthenticationHandler.IsThisComputer(context) && HostAccess.IsThisComputersName(HostAccess.HostName(context)));

    public bool Verify(string? code)
    {
        var expected = Current;
        if (expected is null || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var normalized = Normalize(code);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(normalized), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>Setup is done: the code and its file go away.</summary>
    public void Complete()
    {
        lock (_lock)
        {
            _code = null;
            Delete();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (users.Count() > 0)
            {
                Delete();
                return Task.CompletedTask;
            }

            // An in-process restart (or a restart before setup) keeps the code from the file, so what the tray or an
            // earlier log line shows stays valid.
            _code = ReadExisting() ?? Generate();
            try
            {
                Write(FilePath, _code);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not write {File}: {Message}", FilePath, ex.Message);
            }
        }

        logger.LogWarning(
            "First-run setup: open StreamZH in a browser on this computer to create the owner account. From another device the setup page asks for this setup code: {Code} (also in {File})",
            Display(_code),
            FilePath);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>"ABCD-EFGH-JKLM" for people; the dashes and case do not matter when it is typed.</summary>
    public static string Display(string code) => string.Join('-', code.Chunk(4).Select(c => new string(c)));

    public static string Normalize(string code) =>
        new([.. code.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant)]);

    private static string Generate()
    {
        var chars = new char[Length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    private string? ReadExisting()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var code = Normalize(File.ReadAllText(FilePath));
            return code.Length == Length && code.All(Alphabet.Contains) ? code : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Delete()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("Could not delete {File}: {Message}", FilePath, ex.Message);
        }
    }

    /// <summary>Writes the file so only this account (plus SYSTEM and administrators on Windows) can read it.</summary>
    public static void Write(string path, string code)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(code);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var text = Display(code) + Environment.NewLine;
        if (OperatingSystem.IsWindows())
        {
            WriteWindows(path, text);
            return;
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(text);
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindows(string path, string text)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        if (WindowsIdentity.GetCurrent().User is { } me)
        {
            security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        using var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.WriteData | FileSystemRights.Synchronize, FileShare.None, 4096, FileOptions.None, security);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }
}
