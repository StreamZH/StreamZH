// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Usenet.Pool;

/// <summary>
/// Checks whether articles exist without downloading them (NNTP STAT), for the download engine's "pre-check" switch.
/// Implemented by the same object as the article source; the engine looks for it with a type check.
/// </summary>
public interface IArticleChecker
{
    /// <returns>True when a server has the article, false when none has it.</returns>
    /// <exception cref="Core.Downloads.UsenetServerException">No server could be asked.</exception>
    Task<bool> ExistsAsync(string messageId, DateTimeOffset posted, CancellationToken cancellationToken);
}
