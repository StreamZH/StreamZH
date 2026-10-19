// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;

namespace StreamZH.Library.PostProcessing;

/// <summary>State of an installation, for the settings page and the status window.</summary>
/// <param name="Progress">0..1 while installing.</param>
/// <param name="Step">"Downloading", "Unpacking" or "Installing" while installing.</param>
/// <param name="Error">Why the last installation failed; cleared when a new one starts.</param>
public sealed record ToolInstallStatus(bool Installing, double? Progress, string? Step, string? Error)
{
    public static ToolInstallStatus Idle { get; } = new(false, null, null, null);
}

public sealed record ToolInstallResult(bool Success, string? Error, string? Version);

/// <summary>Content of version.json in a tool folder installed by StreamZH.</summary>
public sealed record InstalledToolInfo(string Name, string Version, string Asset, string Sha256, DateTimeOffset Installed);

/// <summary>An installation failed for a reason the user can read (shown as is).</summary>
public sealed class ToolInstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Installs par2cmdline-turbo, 7-Zip and ffprobe into &lt;data&gt;/tools/par2, /7-Zip and /ffprobe from their official, pinned
/// releases (SHA-256 checked). Downloads and unpacks into &lt;data&gt;/tools/.download first and only then swaps the folder, so a
/// half installation never counts. One installation per tool at a time: a second request joins the running one.
/// </summary>
public sealed class ToolInstaller(
    IHttpClientFactory httpClients,
    IToolProcessRunner runner,
    ToolEnvironment environment,
    ToolCatalog catalog,
    PostProcessTools tools,
    IPostProcessToolSettings settings,
    IEventBus events,
    TimeProvider time,
    ILogger<ToolInstaller> logger) : IDisposable
{
    public const string HttpClientName = "StreamZH.Tools";
    public const string VersionFileName = "version.json";
    public const string DownloadFolderName = ".download";

    public static IReadOnlyList<PostProcessTool> AllTools { get; } = [PostProcessTool.Par2, PostProcessTool.SevenZip, PostProcessTool.FFprobe];

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly Dictionary<PostProcessTool, Task<ToolInstallResult>> _running = [];
    private readonly ConcurrentDictionary<PostProcessTool, ToolInstallStatus> _status = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>The release StreamZH would install here, or null when it cannot install the tool on this system.</summary>
    public ToolPackage? Package(PostProcessTool tool) =>
        environment.InstalledDirectory is null ? null : catalog.Find(tool, environment.Platform);

    public bool IsInstallable(PostProcessTool tool) => Package(tool) is not null;

    public ToolInstallStatus Status(PostProcessTool tool) => _status.GetValueOrDefault(tool) ?? ToolInstallStatus.Idle;

    /// <summary>The version.json of the copy installed by StreamZH, or null when there is none.</summary>
    public InstalledToolInfo? Installed(PostProcessTool tool)
    {
        // 0.12 (V-02): a folder StreamZH did not install (or that changed since) counts as not installed.
        if (environment.InstalledToolDirectory(tool) is not { } folder || !tools.IsVerifiedInstall(tool))
        {
            return null;
        }

        var file = Path.Combine(folder, VersionFileName);
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<InstalledToolInfo>(File.ReadAllText(file), Json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Starts an installation, or returns the one that is already running. The task never fails.</summary>
    public Task<ToolInstallResult> StartInstall(PostProcessTool tool)
    {
        if (Package(tool) is not { } package)
        {
            return Task.FromResult(new ToolInstallResult(false, NotInstallableMessage(tool), null));
        }

        lock (_lock)
        {
            if (_running.TryGetValue(tool, out var running))
            {
                return running;
            }

            _status[tool] = new ToolInstallStatus(true, 0, "Starting", null);
            var task = Task.Run(() => RunAsync(package, _stopping.Token));
            _running[tool] = task;
            return task;
        }
    }

    /// <summary>Starts or joins the installation and waits for it.</summary>
    public Task<ToolInstallResult> InstallAsync(PostProcessTool tool, CancellationToken cancellationToken) =>
        StartInstall(tool).WaitAsync(cancellationToken);

    /// <summary>
    /// Installs every tool that is not found and can be installed here, one after the other (startup). Does nothing when
    /// automatic installation is switched off. Failures are logged and shown in the status window, never thrown.
    /// </summary>
    public async Task InstallMissingAsync(CancellationToken cancellationToken)
    {
        if (!settings.AutoInstall)
        {
            logger.LogDebug("Automatic tool installation is switched off");
            return;
        }

        foreach (var tool in AllTools)
        {
            var location = tools.Find(tool);
            if (location.Found)
            {
                if (await IsOutdatedSevenZipAsync(location, cancellationToken).ConfigureAwait(false))
                {
                    // The pinned copy in <data>/tools comes before PATH in the search, so it is used from now on.
                    logger.LogInformation("{Path} is an old 7-Zip; installing the current one for StreamZH", location.Path);
                    await InstallAsync(tool, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (!IsInstallable(tool))
            {
                logger.LogInformation("{Tool} was not found and StreamZH cannot install it on {Platform}; install it yourself", PostProcessTools.DisplayName(tool), environment.Platform);
                continue;
            }

            await InstallAsync(tool, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Versions before 22 (p7zip 16.02 on older NAS and Linux systems) handle links in archives less carefully.</summary>
    public const int MinimumSevenZipMajor = 22;

    /// <summary>"7-Zip 16.02" is outdated, "7-Zip 23.01" is not; unknown versions are left alone.</summary>
    public static bool IsOutdatedSevenZip(string? version) =>
        version is not null && version.StartsWith("7-Zip ", StringComparison.Ordinal)
        && int.TryParse(version["7-Zip ".Length..].Split('.')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
        && major < MinimumSevenZipMajor;

    // A 7-Zip found on PATH or in Program Files (not one the user configured, not ours).
    private async Task<bool> IsOutdatedSevenZipAsync(ToolLocation location, CancellationToken cancellationToken) =>
        location is { Tool: PostProcessTool.SevenZip, Path: { } path, Source: ToolSource.Path or ToolSource.WindowsDefault }
        && IsInstallable(PostProcessTool.SevenZip)
        && IsOutdatedSevenZip(await tools.GetVersionAsync(path, cancellationToken).ConfigureAwait(false));

    /// <summary>Starts an installation in the background when the tool is missing and automatic installation is on.</summary>
    public bool StartIfMissing(PostProcessTool tool)
    {
        if (!settings.AutoInstall || !IsInstallable(tool) || tools.Find(tool).Found)
        {
            return false;
        }

        _ = StartInstall(tool);
        return true;
    }

    public string NotInstallableMessage(PostProcessTool tool) => environment.InstalledDirectory is null
        ? $"StreamZH has no folder to install {PostProcessTools.DisplayName(tool)} into."
        : $"StreamZH cannot install {PostProcessTools.DisplayName(tool)} on {environment.Platform}. Install it yourself.";

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    // ---- Installation ----

    private async Task<ToolInstallResult> RunAsync(ToolPackage package, CancellationToken cancellationToken)
    {
        var tool = package.Tool;
        var name = PostProcessTools.DisplayName(tool);
        logger.LogInformation("Installing {Name} {Version} ({File})", package.Name, package.Version, package.Download.FileName);
        await PublishAsync().ConfigureAwait(false);
        try
        {
            var folder = await InstallCoreAsync(package, cancellationToken).ConfigureAwait(false);
            _status[tool] = ToolInstallStatus.Idle;
            logger.LogInformation("{Name} {Version} installed in {Folder}", package.Name, package.Version, folder);
            return new ToolInstallResult(true, null, package.Version);
        }
        catch (Exception e)
        {
            var message = Describe(e, cancellationToken);
            _status[tool] = new ToolInstallStatus(false, null, null, message);
            logger.LogWarning(e is ToolInstallException ? null : e, "{Tool} could not be installed: {Error}", name, message);
            return new ToolInstallResult(false, message, null);
        }
        finally
        {
            lock (_lock)
            {
                _running.Remove(tool);
            }

            await PublishAsync().ConfigureAwait(false);
        }
    }

    private async Task<string> InstallCoreAsync(ToolPackage package, CancellationToken cancellationToken)
    {
        var root = environment.InstalledDirectory ?? throw new ToolInstallException(NotInstallableMessage(package.Tool));
        var folderName = ToolEnvironment.InstalledFolderName(package.Tool);
        var downloads = Path.Combine(root, DownloadFolderName);
        RemoveLeftovers(downloads, folderName);

        var work = Path.Combine(downloads, $"{folderName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var source = package.Method switch
            {
                ToolInstallMethod.Zip => await UnpackZipAsync(package, work, cancellationToken).ConfigureAwait(false),
                ToolInstallMethod.WindowsMsi => await UnpackMsiAsync(package, work, cancellationToken).ConfigureAwait(false),
                ToolInstallMethod.SevenZipInstaller => await UnpackInstallerAsync(package, package.Download, work, 0, 0.9, cancellationToken).ConfigureAwait(false),
                ToolInstallMethod.TarXz => await UnpackTarAsync(package, work, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown install method {package.Method}"),
            };

            await ReportAsync(package.Tool, "Installing", 0.95).ConfigureAwait(false);
            var stage = Path.Combine(work, "stage");
            Stage(package, source, work, stage);
            var files = ToolFolderHashes.Compute(stage);
            var folder = Replace(package.Tool, stage, work);

            // 0.12 (V-02): only these files, unchanged, are ever run from <data>/tools.
            tools.Manifest.Record(package.Tool, files);
            return folder;
        }
        finally
        {
            TryDelete(work);
            TryDeleteIfEmpty(downloads);
        }
    }

    private async Task<string> UnpackZipAsync(ToolPackage package, string work, CancellationToken cancellationToken)
    {
        var archive = await DownloadAsync(package.Tool, package.Download, work, 0, 0.9, cancellationToken).ConfigureAwait(false);
        await ReportAsync(package.Tool, "Unpacking", 0.9).ConfigureAwait(false);
        var target = Path.Combine(work, "zip");
        try
        {
            ExtractNeeded(package, archive, target);
        }
        catch (InvalidDataException e)
        {
            throw new ToolInstallException($"{package.Download.FileName} is not a valid zip archive.", e);
        }

        return FindFolder(package, target, package.Download.FileName);
    }

    /// <summary>
    /// Unpacks only the files the tool needs and the license files (the FFmpeg build is 115 MB packed and three times that
    /// unpacked; StreamZH needs ffprobe.exe from it). Entries that would land outside the folder are skipped.
    /// </summary>
    private static void ExtractNeeded(ToolPackage package, string archive, string target)
    {
        var wanted = new HashSet<string>(package.Files.Concat(ToolPackages.NoticeFiles), StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0 || !wanted.Contains(entry.Name))
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!path.StartsWith(root, StringComparison.Ordinal))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    /// <summary>
    /// Windows x64: administrative install of the .msi (msiexec /a, needs no admin rights; Scoop does the same), which only
    /// copies the files. Falls back to unpacking the installer .exe with 7zr.exe when msiexec fails.
    /// </summary>
    private async Task<string> UnpackMsiAsync(ToolPackage package, string work, CancellationToken cancellationToken)
    {
        var msi = await DownloadAsync(package.Tool, package.Download, work, 0, 0.8, cancellationToken).ConfigureAwait(false);
        await ReportAsync(package.Tool, "Unpacking", 0.8).ConfigureAwait(false);
        var target = Path.Combine(work, "msi");
        Directory.CreateDirectory(target);

        string problem;
        try
        {
            var result = await runner.RunAsync(MsiExec, ["/a", msi, "/qn", "TARGETDIR=" + target], work, null, ProcessTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode is 0 or 3010 && TryFindFolder(package, target) is { } folder)
            {
                return folder;
            }

            problem = result.ExitCode is 0 or 3010 ? $"{package.Executable} is missing in the unpacked files" : $"exit code {result.ExitCode}";
        }
        catch (Exception e) when (e is Win32Exception or IOException or TimeoutException or UnauthorizedAccessException)
        {
            problem = e.Message;
        }

        if (package.Fallback is not { } fallback)
        {
            throw new ToolInstallException($"msiexec could not unpack {package.Download.FileName} ({problem}).");
        }

        logger.LogInformation("msiexec could not unpack {File} ({Problem}); unpacking {Fallback} with 7zr.exe instead", package.Download.FileName, problem, fallback.FileName);
        return await UnpackInstallerAsync(package, fallback, work, 0.8, 0.9, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The 7-Zip installer .exe is a 7z self-extracting archive: 7zr.exe x installer.exe -o&lt;folder&gt; -y.</summary>
    private async Task<string> UnpackInstallerAsync(
        ToolPackage package, ToolDownload installer, string work, double from, double to, CancellationToken cancellationToken)
    {
        var helper = package.Helper ?? throw new InvalidOperationException($"{package.Name} has no unpacker.");
        var middle = from + ((to - from) * 0.05);
        var sevenZr = await DownloadAsync(package.Tool, helper, work, from, middle, cancellationToken).ConfigureAwait(false);
        var exe = await DownloadAsync(package.Tool, installer, work, middle, to, cancellationToken).ConfigureAwait(false);
        await ReportAsync(package.Tool, "Unpacking", to).ConfigureAwait(false);

        var target = Path.Combine(work, "exe");
        Directory.CreateDirectory(target);
        ToolProcessResult result;
        try
        {
            result = await runner.RunAsync(sevenZr, ["x", exe, "-o" + target, "-y"], work, null, ProcessTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new ToolInstallException($"{helper.FileName} could not be started: {e.Message}", e);
        }
        catch (TimeoutException e)
        {
            throw new ToolInstallException($"{helper.FileName} did not finish unpacking {installer.FileName}.", e);
        }

        if (result.ExitCode != 0)
        {
            throw new ToolInstallException($"{helper.FileName} could not unpack {installer.FileName} (exit code {result.ExitCode}): {Par2Repairer.LastLine(result.Output)}");
        }

        return FindFolder(package, target, installer.FileName);
    }

    /// <summary>Linux and macOS: tar -xJf (needs tar with xz support; macOS tar has it built in).</summary>
    private async Task<string> UnpackTarAsync(ToolPackage package, string work, CancellationToken cancellationToken)
    {
        var archive = await DownloadAsync(package.Tool, package.Download, work, 0, 0.9, cancellationToken).ConfigureAwait(false);
        await ReportAsync(package.Tool, "Unpacking", 0.9).ConfigureAwait(false);
        var target = Path.Combine(work, "tar");
        Directory.CreateDirectory(target);

        const string install = "Install tar and xz (for example: sudo apt install tar xz-utils) and try again, or install 7-Zip yourself.";
        ToolProcessResult result;
        try
        {
            result = await runner.RunAsync("tar", ["-xJf", archive, "-C", target], work, null, ProcessTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception e)
        {
            throw new ToolInstallException($"tar was not found; it is needed to unpack {package.Download.FileName}. {install}", e);
        }
        catch (TimeoutException e)
        {
            throw new ToolInstallException($"tar did not finish unpacking {package.Download.FileName}.", e);
        }

        if (result.ExitCode != 0)
        {
            var output = result.Output;
            var noXz = output.Contains("Cannot exec", StringComparison.OrdinalIgnoreCase)
                || output.Contains("xz: not found", StringComparison.OrdinalIgnoreCase)
                || output.Contains("xz: No such file", StringComparison.OrdinalIgnoreCase);
            throw new ToolInstallException(noXz
                ? $"tar could not unpack {package.Download.FileName} because xz is missing. {install}"
                : $"tar could not unpack {package.Download.FileName} (exit code {result.ExitCode}): {Par2Repairer.LastLine(output)}");
        }

        return FindFolder(package, target, package.Download.FileName);
    }

    /// <summary>Downloads one file and checks its SHA-256; a mismatch deletes the file and stops the installation.</summary>
    private async Task<string> DownloadAsync(
        PostProcessTool tool, ToolDownload download, string folder, double from, double to, CancellationToken cancellationToken)
    {
        await ReportAsync(tool, "Downloading", from).ConfigureAwait(false);
        logger.LogDebug("Downloading {Url}", download.Url);

        var client = httpClients.CreateClient(HttpClientName);
        using var response = await client.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ToolInstallException($"Download of {download.FileName} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.".Replace(" .", ".", StringComparison.Ordinal));
        }

        var total = response.Content.Headers.ContentLength;
        var path = Path.Combine(folder, download.FileName);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (input.ConfigureAwait(false))
        {
            var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (output.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                    {
                        await ReportAsync(tool, "Downloading", from + ((to - from) * Math.Min(1, done / (double)total.Value))).ConfigureAwait(false);
                    }
                }
            }
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, download.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(path);
            throw new ToolInstallException(
                $"{download.FileName} does not match its SHA-256 checksum (expected {download.Sha256}, got {actual}). Nothing was installed.");
        }

        await ReportAsync(tool, "Downloading", to).ConfigureAwait(false);
        return path;
    }

    // ---- Files ----

    private static string MsiExec => Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    /// <summary>The folder that contains the executable (closest to the top) and all other required files.</summary>
    private static string FindFolder(ToolPackage package, string root, string from) =>
        TryFindFolder(package, root) ?? throw new ToolInstallException($"{string.Join(" and ", package.Files)} not found in {from}.");

    private static string? TryFindFolder(ToolPackage package, string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(root, package.Executable, options)
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .OrderBy(folder => folder.Length)
            .FirstOrDefault(folder => package.Files.All(file => FindFile(folder, file) is not null));
    }

    /// <summary>A license file in the tool's folder, else in the folders above it up to (not including) the work folder.</summary>
    private static string? NoticeFile(string source, string work, string name)
    {
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(work));
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        while (folder.Length > top.Length && folder.StartsWith(top, StringComparison.Ordinal))
        {
            if (FindFile(folder, name) is { } path)
            {
                return path;
            }

            folder = Path.GetDirectoryName(folder) ?? top;
        }

        return null;
    }

    private static string? FindFile(string folder, string name)
    {
        var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(folder, name, options).FirstOrDefault();
    }

    /// <summary>
    /// Copies the required files into the stage folder, plus the license files next to them or in a folder above (the
    /// FFmpeg build keeps LICENSE beside bin/), and writes version.json.
    /// </summary>
    private void Stage(ToolPackage package, string source, string work, string stage)
    {
        Directory.CreateDirectory(stage);
        foreach (var file in package.Files)
        {
            var target = Path.Combine(stage, file);
            File.Copy(FindFile(source, file)!, target);
            if (!OperatingSystem.IsWindows() && !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        var copied = new HashSet<string>(package.Files, StringComparer.OrdinalIgnoreCase);
        foreach (var notice in ToolPackages.NoticeFiles)
        {
            if (NoticeFile(source, work, notice) is { } path && copied.Add(Path.GetFileName(path)))
            {
                File.Copy(path, Path.Combine(stage, Path.GetFileName(path)));
            }
        }

        var info = new InstalledToolInfo(package.Name, package.Version, package.Download.FileName, package.Download.Sha256, time.GetUtcNow());
        File.WriteAllText(Path.Combine(stage, VersionFileName), JsonSerializer.Serialize(info, Json));
    }

    /// <summary>Swaps the finished stage folder in; the old copy is moved aside first and put back if that fails.</summary>
    private string Replace(PostProcessTool tool, string stage, string work)
    {
        var target = environment.InstalledToolDirectory(tool)!;
        var old = Path.Combine(work, "old");
        if (Directory.Exists(target))
        {
            try
            {
                Directory.Move(target, old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ToolInstallException(
                    $"The installed copy in {target} is in use (a repair or extraction may be running). Try again when it has finished.", e);
            }
        }

        try
        {
            Directory.Move(stage, target);
        }
        catch
        {
            if (Directory.Exists(old) && !Directory.Exists(target))
            {
                Directory.Move(old, target);
            }

            throw;
        }

        return target;
    }

    private void RemoveLeftovers(string downloads, string folderName)
    {
        if (!Directory.Exists(downloads))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(downloads, folderName + "-*"))
        {
            TryDelete(folder);
        }
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(e, "Could not delete {Folder}", folder);
        }
    }

    private static void TryDeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another installation may have just started; it cleans up after itself.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The whole work folder is deleted afterwards anyway.
        }
    }

    // ---- Progress ----

    /// <summary>Updates the progress; the status window is told in 5 % steps and when the step changes.</summary>
    private async ValueTask ReportAsync(PostProcessTool tool, string step, double progress)
    {
        var previous = Status(tool);
        var next = new ToolInstallStatus(true, Math.Clamp(progress, 0, 1), step, null);
        _status[tool] = next;
        if (previous.Step != step || Math.Floor((previous.Progress ?? 0) * 20) != Math.Floor(next.Progress!.Value * 20))
        {
            await PublishAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask PublishAsync()
    {
        try
        {
            await events.PublishAsync(new OngoingChanged(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            logger.LogDebug(e, "Could not publish the installation progress");
        }
    }

    private static string Describe(Exception e, CancellationToken cancellationToken) => e switch
    {
        ToolInstallException => e.Message,
        OperationCanceledException when cancellationToken.IsCancellationRequested => "Cancelled because StreamZH is stopping.",
        OperationCanceledException or TimeoutException => "The download timed out.",
        HttpRequestException => $"Download failed: {e.Message}",
        IOException or UnauthorizedAccessException => $"Could not write the files: {e.Message}",
        _ => e.Message,
    };
}
