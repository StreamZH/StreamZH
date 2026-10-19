// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Library.Downloads;

namespace StreamZH.Torrent;

/// <summary>
/// The "StreamZH" download client: the built-in <see cref="TorrentEngine"/> behind <see cref="ITorrentClient"/>, so the
/// torrent tracker and grabbing treat it like qBittorrent. The definition's settings are not needed (the engine has
/// its own settings page); everything runs in this process.
/// </summary>
public sealed class BuiltInTorrentClient(TorrentEngine engine, TorrentEngineSettings settings, ITorrentSavePaths savePaths) : ITorrentClient
{
    public string Implementation => DownloadClientDefinition.BuiltInTorrent;

    /// <exception cref="DownloadClientException">The listen port is used by another program, or the data folder is not writable.</exception>
    public Task<TorrentClientInfo> TestAsync(DownloadClientDefinition client, CancellationToken cancellationToken)
    {
        if (!engine.ListenPortAvailable())
        {
            throw new DownloadClientException(
                $"Port {settings.Current.ListenPort} is used by another program. Choose another port in the torrent engine settings.",
                "listenPort");
        }

        try
        {
            Directory.CreateDirectory(engine.DataFolder);
            var probe = Path.Combine(engine.DataFolder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DownloadClientException($"StreamZH cannot write to {engine.DataFolder}: {ex.Message}", null, ex);
        }

        return Task.FromResult(new TorrentClientInfo("StreamZH", $"built-in (MonoTorrent {TorrentEngine.EngineVersion})", savePaths.Categories()));
    }

    public Task<string> AddAsync(DownloadClientDefinition client, TorrentAddRequest request, CancellationToken cancellationToken) =>
        engine.AddAsync(request, cancellationToken);

    public Task<IReadOnlyList<TorrentStatus>> GetTorrentsAsync(DownloadClientDefinition client, string? category, CancellationToken cancellationToken) =>
        engine.GetTorrentsAsync(category, cancellationToken);

    public async Task RemoveAsync(DownloadClientDefinition client, string hash, bool deleteData, CancellationToken cancellationToken) =>
        await engine.RemoveAsync(hash, deleteData, cancellationToken);
}

/// <summary>A torrent of a category goes into that category's completed folder (Settings → Downloads → Categories).</summary>
public sealed class CategoryTorrentSavePaths(DownloadCategoryService categories) : ITorrentSavePaths
{
    public string FolderFor(string? category)
    {
        var name = string.IsNullOrWhiteSpace(category) ? DownloadCategoryService.Other : category.Trim();
        return categories.FolderOf(categories.Resolve(DownloadCategoryService.KindOf(name), name));
    }

    public IReadOnlyList<string> Categories() => [.. categories.All().Select(c => c.Name)];

    public string? Problem(string folder) => categories.FolderProblem(folder);
}
