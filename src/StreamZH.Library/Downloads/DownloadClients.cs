// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;
using StreamZH.Core.Downloads;
using StreamZH.Library.Search;

namespace StreamZH.Library.Downloads;

/// <summary>Hands releases to StreamZH's own download engine. The reference is the download id.</summary>
public sealed class BuiltInDownloadClient(DownloadEngine engine) : IDownloadClient
{
    public const string ClientName = "StreamZH";

    public string Name => ClientName;

    /// <exception cref="LibraryException">The NZB is empty or invalid.</exception>
    public async Task<string> AddAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        var item = await engine.AddAsync(request, cancellationToken);
        return item.Id.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Writes the NZB into a folder another downloader (SABnzbd, NZBGet) watches. A password is put into the file name
/// as "title{{password}}.nzb", which both understand. The reference is the path of the written file.
/// </summary>
public sealed class BlackholeDownloadClient(DownloadSettings settings) : IDownloadClient
{
    public const string ClientName = "Blackhole folder";

    public string Name => ClientName;

    /// <exception cref="LibraryException">The folder is not writable.</exception>
    public Task<string> AddAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var folder = settings.NzbFolder;
        var title = string.IsNullOrWhiteSpace(request.Password) ? request.Title : $"{request.Title}{{{{{request.Password}}}}}";
        try
        {
            return Task.FromResult(NzbFileNames.WriteNew(folder, title, request.Nzb));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LibraryException($"Could not write the NZB file to {folder}: {ex.Message}", LibraryError.Invalid);
        }
    }
}

/// <summary>The client chosen in the download settings.</summary>
public sealed class DownloadClientProvider(DownloadSettings settings, BuiltInDownloadClient builtIn, BlackholeDownloadClient blackhole)
{
    public IDownloadClient Current => settings.Client == DownloadClientKind.Blackhole ? blackhole : builtIn;
}

/// <summary>The "{{password}}" convention of indexers and SABnzbd: "Some.Release{{secret}}" means the archives use "secret".</summary>
public static partial class NzbPassword
{
    public static (string Title, string? Password) Split(string title)
    {
        var match = PasswordRegex().Match(title);
        return match.Success && match.Groups[1].Value.Trim().Length > 0
            ? (match.Groups[1].Value.Trim(), match.Groups[2].Value)
            : (title, null);
    }

    [GeneratedRegex(@"^(.*?)\s*\{\{(.+)\}\}\s*$")]
    private static partial Regex PasswordRegex();
}
