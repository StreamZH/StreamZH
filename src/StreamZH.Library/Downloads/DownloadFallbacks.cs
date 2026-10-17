// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Usenet.Nzb;

namespace StreamZH.Library.Downloads;

/// <summary>Stand-in until a Usenet server module is registered: no connections, so the queue waits.</summary>
public sealed class UnconfiguredArticleSource : IArticleSource
{
    public int MaxConnections => 0;

    public Task<DecodedArticle> FetchAsync(string messageId, IReadOnlyList<string> groups, DateTimeOffset posted, CancellationToken cancellationToken) =>
        throw new UsenetServerException("No Usenet server is configured.");
}

/// <summary>
/// Stand-in until the par2/unpack module is registered: moves the downloaded files (without par2 and sfv files) into
/// the target folder. No verification, no repair, no unpacking.
/// </summary>
public sealed class MoveOnlyPostProcessor : IPostProcessor
{
    public Task<PostProcessResult> RunAsync(PostProcessJob job, IProgress<PostProcessProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(progress);
        try
        {
            Directory.CreateDirectory(job.TargetFolder);
            var files = Directory.Exists(job.WorkFolder) ? Directory.GetFiles(job.WorkFolder) : [];
            var moved = new List<string>();
            for (var i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new PostProcessProgress(PostProcessStage.Cleaning, (double)i / files.Length, "Moving files"));
                var kind = NzbFileClassifier.Classify(Path.GetFileName(files[i]));
                if (kind is NzbFileKind.Par2Index or NzbFileKind.Par2Recovery or NzbFileKind.Sfv)
                {
                    continue;
                }

                var target = Path.Combine(job.TargetFolder, Path.GetFileName(files[i]));
                File.Move(files[i], target, overwrite: true);
                moved.Add(target);
            }

            return Task.FromResult(new PostProcessResult(true, null, job.TargetFolder, moved, false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new PostProcessResult(false, $"Could not move the files to {job.TargetFolder}: {ex.Message}", job.TargetFolder, [], false));
        }
    }
}
