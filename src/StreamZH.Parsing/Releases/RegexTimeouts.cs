// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Parsing.Releases;

/// <summary>
/// Release names come from the internet, so every release regex runs with a match timeout.
/// A pattern that times out is treated as "no match" instead of hanging the caller.
/// </summary>
internal static class RegexTimeouts
{
    public const int Milliseconds = 1000;
}
