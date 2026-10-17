// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Host.Status;

/// <summary>Everything the status window shows: what is running right now and what needs attention.</summary>
public sealed record OngoingResource(IReadOnlyList<OngoingRunning> Running, IReadOnlyList<OngoingNotice> Notices);

/// <summary>One running row.</summary>
/// <param name="Kind">"job", "download", "postprocess" or "install" (par2 or 7-Zip being installed).</param>
/// <param name="Progress">0..1, or null when the work has no measurable progress.</param>
public sealed record OngoingRunning(
    string Key,
    string Kind,
    string Title,
    string? Sub,
    double? Progress,
    long? SpeedBps,
    double? EtaSeconds,
    string? Link);

/// <summary>One "Heads-up" row.</summary>
/// <param name="Level">"info", "warn" or "error".</param>
/// <param name="Dismissible">The UI may hide it (remembered per browser by <see cref="Key"/>).</param>
public sealed record OngoingNotice(string Key, string Level, string Title, string? Sub, string? Link, bool Dismissible);

public static class OngoingKinds
{
    public const string Job = "job";
    public const string Download = "download";
    public const string PostProcess = "postprocess";
    public const string Install = "install";
}

public static class NoticeLevels
{
    public const string Info = "info";
    public const string Warn = "warn";
    public const string Error = "error";
}
