// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using StreamZH.Core;
using StreamZH.Core.Net;
using StreamZH.Data;

namespace StreamZH.Library.Trash;

/// <param name="Changed">A new copy of the guides is in place.</param>
/// <param name="Error">Why the download failed (the last good copy stays in use).</param>
public sealed record TrashDownloadResult(bool Changed, string? Commit, string? Error);

/// <summary>
/// The local copy of the TRaSH Guides in <c>&lt;data&gt;/trash/&lt;commit&gt;/</c>. Recyclarr clones the git repository;
/// StreamZH downloads the branch archive (<c>{repository}/archive/refs/heads/{branch}.zip</c>) through the app's HTTP
/// client (proxy of Settings → General) and keeps only what the sync reads: <c>metadata.json</c>, the folders it names,
/// <c>docs/json/**</c> and the two "Collection of Custom Formats" pages. For GitHub it first asks for the branch's commit
/// and skips the download when it did not change; otherwise the ETag of the last download is sent. A failed download
/// keeps the last good copy. A local folder works as repository too (offline mirrors, development).
/// </summary>
public sealed class TrashGuideStore(
    AppPaths paths, ConfigRepository config, TrashSettings settings, IHttpClientFactory httpClients, TimeProvider time, ILogger<TrashGuideStore> logger)
{
    public const string HttpClientName = "trash-guides";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _cacheLock = new();
    private TrashGuides? _cached;
    private string? _cachedFolder;

    /// <summary>Folder with all copies; only the current one is kept after a sync.</summary>
    public string Root => Path.Combine(paths.DataDirectory, "trash");

    /// <summary>The folder of the copy in use, null when the guides were never downloaded.</summary>
    public string? CurrentFolder =>
        config.Get(ConfigKeys.TrashFolder) is { Length: > 0 } name && Directory.Exists(Path.Combine(Root, name)) ? Path.Combine(Root, name) : null;

    public bool HasCopy => CurrentFolder is not null;

    public string? Commit => config.Get(ConfigKeys.TrashCommit) is { Length: > 0 } commit ? commit : null;

    public DateTimeOffset? LastSync =>
        DateTimeOffset.TryParse(config.Get(ConfigKeys.TrashLastSync), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    public string? LastError => config.Get(ConfigKeys.TrashLastError) is { Length: > 0 } error ? error : null;

    /// <summary>The parsed guides of the current copy (empty without a copy); parsed once per copy.</summary>
    public TrashGuides Current
    {
        get
        {
            var folder = CurrentFolder;
            lock (_cacheLock)
            {
                if (folder is null)
                {
                    return TrashGuides.Empty;
                }

                if (_cached is null || !string.Equals(_cachedFolder, folder, StringComparison.Ordinal))
                {
                    try
                    {
                        _cached = TrashGuideParser.Parse(folder);
                    }
                    catch (FormatException ex)
                    {
                        logger.LogWarning("The local copy of the TRaSH Guides could not be read: {Error}", ex.Message);
                        _cached = TrashGuides.Empty;
                    }

                    _cachedFolder = folder;
                }

                return _cached;
            }
        }
    }

    /// <summary>Records a problem of the sync (shown in the status and as a notice); null clears it.</summary>
    public void SetError(string? error) => config.Set(ConfigKeys.TrashLastError, error ?? string.Empty);

    /// <summary>Downloads the guides when they changed (or when there is no copy yet).</summary>
    public async Task<TrashDownloadResult> DownloadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = settings.Values;
            var source = $"{values.Repository}#{values.Branch}";
            var sameSource = string.Equals(config.Get(ConfigKeys.TrashSource), source, StringComparison.Ordinal) && HasCopy;
            try
            {
                var download = Path.IsPathFullyQualified(values.Repository)
                    ? CopyLocal(values.Repository, sameSource)
                    : await DownloadArchiveAsync(values.Repository, values.Branch, sameSource, cancellationToken);

                if (download is not null)
                {
                    Install(download, source);
                }

                config.Set(ConfigKeys.TrashLastSync, time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
                SetError(null);
                if (download is null)
                {
                    logger.LogDebug("TRaSH Guides unchanged ({Commit})", Commit);
                }

                return new TrashDownloadResult(download is not null, Commit, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or FormatException or UnauthorizedAccessException
                                           or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
            {
                var message = ex switch
                {
                    TaskCanceledException => "The download of the TRaSH Guides timed out.",
                    HttpRequestException http => $"The TRaSH Guides could not be downloaded: {http.Message}",
                    _ => $"The TRaSH Guides could not be read: {ex.Message}",
                };
                logger.LogWarning("{Message}{Kept}", message, HasCopy ? " The last copy stays in use." : string.Empty);
                SetError(message);
                return new TrashDownloadResult(false, Commit, message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Download ----

    private sealed record NewCopy(string Folder, string? Commit, string? ETag);

    private async Task<NewCopy?> DownloadArchiveAsync(string repository, string branch, bool sameSource, CancellationToken cancellationToken)
    {
        var client = httpClients.CreateClient(HttpClientName);
        var stored = Commit;

        // GitHub tells the commit of a branch in a small request; the archive is only fetched when it changed.
        if (sameSource && stored is not null && GitHubApiCommitUrl(repository, branch) is { } commitUrl)
        {
            var latest = await TryGetCommitAsync(client, commitUrl, cancellationToken);
            if (latest is not null && string.Equals(latest, stored, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var url = ArchiveUrl(repository, branch);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (sameSource && config.Get(ConfigKeys.TrashETag) is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                response.StatusCode == HttpStatusCode.NotFound
                    ? $"{url} was not found (check the repository and the branch)."
                    : $"{url} answered HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        Directory.CreateDirectory(Root);
        var zipFile = Path.Combine(Root, $"download-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var file = File.Create(zipFile))
            await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                await ResponseLimits.CopyAsync(body, file, ResponseLimits.TrashArchive, "The TRaSH Guides archive", cancellationToken);
            }

            using var archive = ZipFile.OpenRead(zipFile);
            var commit = IsCommit(archive.Comment) ? archive.Comment.Trim().ToLowerInvariant() : null;
            var entries = archive.Entries.Where(e => e.FullName.Length > 0 && !e.FullName.EndsWith('/')).ToList();

            // GitHub archives put everything below one folder ("Guides-master/").
            var prefix = entries.Select(e => e.FullName).FirstOrDefault(n => n.EndsWith("/" + TrashGuideParser.MetadataFile, StringComparison.Ordinal)
                                                                             && n.Count(c => c == '/') == 1) is { } meta
                ? meta[..(meta.Length - TrashGuideParser.MetadataFile.Length)]
                : entries.Any(e => e.FullName == TrashGuideParser.MetadataFile) ? string.Empty
                : throw new FormatException("The archive has no metadata.json; is this the TRaSH Guides repository?");

            var files = entries
                .Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal))
                .Select(e => (Path: e.FullName[prefix.Length..], Open: (Func<Stream>)e.Open))
                .ToList();
            var etagText = response.Headers.ETag?.ToString();
            return new NewCopy(Extract(files), commit, etagText);
        }
        finally
        {
            TryDelete(zipFile);
        }
    }

    private static async Task<string?> TryGetCommitAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.sha"));
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = (await ResponseLimits.ReadStringAsync(response.Content, 4096, "The commit", cancellationToken)).Trim();
            return IsCommit(text) ? text.ToLowerInvariant() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            return null; // Rate limit or no API access: the archive request decides.
        }
    }

    /// <summary>A copy from a local folder (a clone or an extracted archive); re-copied unless its git commit is unchanged.</summary>
    private NewCopy? CopyLocal(string folder, bool sameSource)
    {
        if (!File.Exists(Path.Combine(folder, TrashGuideParser.MetadataFile)))
        {
            throw new FormatException($"{folder} has no metadata.json.");
        }

        var commit = LocalGitCommit(folder);
        if (sameSource && commit is not null && string.Equals(commit, Commit, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Where(r => !r.StartsWith(".git/", StringComparison.Ordinal))
            .Select(r => (Path: r, Open: (Func<Stream>)(() => File.OpenRead(Path.Combine(folder, r)))))
            .ToList();
        return new NewCopy(Extract(files), commit, null);
    }

    /// <summary>Writes the wanted files into a new hidden folder below <see cref="Root"/> and checks that they parse.</summary>
    private string Extract(IReadOnlyList<(string Path, Func<Stream> Open)> files)
    {
        var metadata = files.FirstOrDefault(f => f.Path == TrashGuideParser.MetadataFile);
        if (metadata.Open is null)
        {
            throw new FormatException("metadata.json is missing.");
        }

        string metadataJson;
        using (var input = metadata.Open())
        using (var buffer = new MemoryStream())
        {
            ResponseLimits.Copy(input, buffer, ResponseLimits.TrashFile, TrashGuideParser.MetadataFile);
            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8);
            metadataJson = reader.ReadToEnd();
        }

        var keep = TrashGuideParser.MetadataPaths(metadataJson).Append("docs/json").ToList();
        var categories = TrashGuideParser.CategoryFiles.Values.ToHashSet(StringComparer.Ordinal);
        bool Wanted(string path) =>
            path == TrashGuideParser.MetadataFile || categories.Contains(path)
            || keep.Any(k => path == k || path.StartsWith(k + "/", StringComparison.Ordinal));

        Directory.CreateDirectory(Root);
        var target = Path.Combine(Root, $".new-{Guid.NewGuid():N}");
        var targetFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        try
        {
            foreach (var (path, open) in files.Where(f => Wanted(f.Path)))
            {
                var destination = Path.GetFullPath(Path.Combine(target, path.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(targetFull, StringComparison.Ordinal))
                {
                    continue; // "../" in an archive entry
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = open();
                using var output = File.Create(destination);
                ResponseLimits.Copy(input, output, ResponseLimits.TrashFile, path);
            }

            var guides = TrashGuideParser.Parse(target);
            if (guides.Movie.CustomFormats.Count + guides.Series.CustomFormats.Count == 0
                || guides.Movie.Profiles.Count + guides.Series.Profiles.Count == 0)
            {
                throw new FormatException("The download contains no custom formats or quality profiles.");
            }

            return target;
        }
        catch
        {
            TryDeleteFolder(target);
            throw;
        }
    }

    /// <summary>Moves the new copy to its final folder, makes it current and removes older copies.</summary>
    private void Install(NewCopy copy, string source)
    {
        var name = copy.Commit is { } commit
            ? commit
            : copy.ETag is { Length: > 0 } etag
                ? "etag-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(etag)))[..16]
                : "copy-" + time.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var final = Path.Combine(Root, name);
        if (Directory.Exists(final))
        {
            TryDeleteFolder(final);
        }

        Directory.Move(copy.Folder, final);
        config.Set(ConfigKeys.TrashFolder, name);
        config.Set(ConfigKeys.TrashCommit, copy.Commit ?? string.Empty);
        config.Set(ConfigKeys.TrashETag, copy.ETag ?? string.Empty);
        config.Set(ConfigKeys.TrashSource, source);
        logger.LogInformation("TRaSH Guides updated to {Version}", copy.Commit ?? name);

        foreach (var old in Directory.EnumerateDirectories(Root).Where(d => !string.Equals(Path.GetFileName(d), name, StringComparison.Ordinal)))
        {
            TryDeleteFolder(old);
        }

        foreach (var leftover in Directory.EnumerateFiles(Root, "download-*.zip"))
        {
            TryDelete(leftover);
        }
    }

    // ---- Helpers ----

    /// <summary><c>https://github.com/o/r</c> + master → <c>https://github.com/o/r/archive/refs/heads/master.zip</c>.</summary>
    public static string ArchiveUrl(string repository, string branch) =>
        $"{TrashSettings.NormalizeRepository(repository)}/archive/refs/heads/{Uri.EscapeDataString(branch).Replace("%2F", "/", StringComparison.Ordinal)}.zip";

    /// <summary>The GitHub API address of the branch's latest commit; null for other hosts.</summary>
    public static string? GitHubApiCommitUrl(string repository, string branch)
    {
        if (!Uri.TryCreate(TrashSettings.NormalizeRepository(repository), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 2 ? $"https://api.github.com/repos/{parts[0]}/{parts[1]}/commits/{Uri.EscapeDataString(branch)}" : null;
    }

    private static bool IsCommit(string? text) =>
        text is not null && text.Trim() is { Length: 40 } t && t.All(Uri.IsHexDigit);

    private static string? LocalGitCommit(string folder)
    {
        try
        {
            var head = Path.Combine(folder, ".git", "HEAD");
            if (!File.Exists(head))
            {
                return null;
            }

            var text = File.ReadAllText(head).Trim();
            if (IsCommit(text))
            {
                return text.ToLowerInvariant();
            }

            if (!text.StartsWith("ref: ", StringComparison.Ordinal))
            {
                return null;
            }

            var reference = text[5..].Trim();
            var refFile = Path.Combine(folder, ".git", reference.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refFile) && File.ReadAllText(refFile).Trim() is var sha && IsCommit(sha))
            {
                return sha.ToLowerInvariant();
            }

            var packed = Path.Combine(folder, ".git", "packed-refs");
            return File.Exists(packed)
                ? File.ReadLines(packed).Select(l => l.Split(' ')).Where(p => p.Length == 2 && p[1] == reference && IsCommit(p[0]))
                    .Select(p => p[0].ToLowerInvariant()).FirstOrDefault()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed with the next sync.
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed with the next sync.
        }
    }
}
