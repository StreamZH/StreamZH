// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library.Downloads;

namespace StreamZH.Library.PostProcessing;

/// <summary>
/// The global post-processing switches (Settings → Downloads → Post-processing). Per-download choices (how much is
/// done, keep files) come with the <c>PostProcessJob</c>.
/// </summary>
/// <param name="RecursiveUnpack">Archives found inside archives are unpacked too (up to <see cref="PostProcessor.MaxNestedLevels"/> levels).</param>
/// <param name="IgnoreSamples">Sample files are not moved to the completed folder.</param>
/// <param name="CleanupExtensions">Deleted after unpacking instead of being moved.</param>
/// <param name="Deobfuscate">An obfuscated final file name (random letters and digits) is replaced by the download's name.</param>
/// <param name="UnwantedExtensions">Checked in the unpacked files; <paramref name="UnwantedAction"/> says what happens.</param>
public sealed record PostProcessSwitches(
    bool RecursiveUnpack,
    bool IgnoreSamples,
    IReadOnlySet<string> CleanupExtensions,
    bool Deobfuscate,
    IReadOnlySet<string> UnwantedExtensions,
    UnwantedAction UnwantedAction,
    IReadOnlyList<string> Par2Arguments,
    IReadOnlyList<string> SevenZipArguments,
    ToolPriority ToolPriority)
{
    /// <summary>The behaviour before these switches existed.</summary>
    public static PostProcessSwitches Default { get; } = new(
        true,
        true,
        new HashSet<string>(Search.DownloadSettings.DefaultCleanupList, StringComparer.OrdinalIgnoreCase),
        false,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        UnwantedAction.Off,
        [],
        [],
        ToolPriority.Normal);
}

/// <summary>Source of the current <see cref="PostProcessSwitches"/> (the download settings in the app, a fixed value in tests).</summary>
public interface IPostProcessSwitches
{
    PostProcessSwitches PostProcess { get; }
}

/// <summary>Fixed switches.</summary>
public sealed class FixedPostProcessSwitches(PostProcessSwitches switches) : IPostProcessSwitches
{
    public static FixedPostProcessSwitches Default { get; } = new(PostProcessSwitches.Default);

    public PostProcessSwitches PostProcess { get; set; } = switches;
}
