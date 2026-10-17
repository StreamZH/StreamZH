// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library;
using StreamZH.Library.Downloads;
using StreamZH.Library.PostProcessing;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library.Downloads;

public sealed class DownloadSettingsTests : IDisposable
{
    private readonly DownloadTestBed _bed = new();

    private DownloadSettings Settings => _bed.Settings;

    [Fact]
    public void Defaults_keep_the_behaviour_from_before()
    {
        Assert.Null(Settings.NzbBackupFolder);
        Assert.Null(Settings.WatchFolder);
        Assert.Equal(TimeSpan.FromSeconds(5), Settings.WatchInterval);
        Assert.False(Settings.PauseOnStart);
        Assert.False(Settings.PreCheck);
        Assert.Equal(0, Settings.MaxActiveDownloads);
        Assert.Equal(TimeSpan.Zero, Settings.PropagationDelay);
        Assert.Equal(DuplicateAction.Off, Settings.DuplicateAction);
        Assert.Equal(QueueSort.Off, Settings.AutoSort);
        Assert.Equal(UnwantedAction.Off, Settings.UnwantedAction);
        Assert.Contains(".exe", Settings.UnwantedExtensions);
        Assert.Equal(EncryptedAction.Off, Settings.EncryptedAction);
        Assert.False(Settings.DownloadAllPar2);
        Assert.True(Settings.OnlyVerified);
        Assert.False(Settings.TemporaryFolderName);
        Assert.Equal(ToolPriority.Normal, Settings.ToolPriority);
        Assert.Equal(1, Settings.PostProcessWorkers);
        Assert.Equal(0, Settings.MaxLineSpeedKBps);
        Assert.Equal((0, 0, true), (Settings.HistoryKeepDays, Settings.HistoryKeepItems, Settings.HistoryKeepFailed));
        Assert.True(Settings.AbortHopeless);
        Assert.False(Settings.KeepFiles);
        Assert.Equal(3, Settings.ArticleTries);

        var pp = Settings.PostProcess;
        Assert.True(pp.RecursiveUnpack);
        Assert.True(pp.IgnoreSamples);
        Assert.False(pp.Deobfuscate);
        Assert.Equal(PostProcessSwitches.Default.CleanupExtensions.Order(), pp.CleanupExtensions.Order());
        Assert.Empty(pp.Par2Arguments);
        Assert.Empty(pp.SevenZipArguments);
    }

    [Fact]
    public void Every_switch_is_saved_and_read_back()
    {
        var backup = Path.Combine(_bed.DataDirectory, "backup");
        Settings.Update(new DownloadSettingsChange
        {
            NzbBackupFolder = backup,
            WatchIntervalSeconds = 30,
            PauseOnStart = true,
            PreCheck = true,
            MaxActiveDownloads = 1,
            PropagationDelayMinutes = 15,
            DuplicateAction = "Pause",
            AutoSort = "newest",
            UnwantedExtensions = "EXE *.scr; .bat",
            UnwantedAction = "warn",
            EncryptedAction = "fail",
            DownloadAllPar2 = true,
            OnlyVerified = false,
            RecursiveUnpack = false,
            IgnoreSamples = false,
            CleanupList = ".nfo .txt",
            Deobfuscate = true,
            TemporaryFolderName = true,
            Par2Arguments = "-t4",
            SevenZipArguments = "-mmt2 -bb1",
            ToolPriority = "idle",
            PostProcessWorkers = 2,
            MaxLineSpeedKBps = 50_000,
            HistoryKeepDays = 30,
            HistoryKeepItems = 500,
            HistoryKeepFailed = false,
        });

        // A fresh instance reads what was stored.
        var stored = new DownloadSettings(_bed.Config, _bed.Paths);
        Assert.Equal(backup, stored.NzbBackupFolder);
        Assert.Equal(TimeSpan.FromSeconds(30), stored.WatchInterval);
        Assert.True(stored.PauseOnStart && stored.PreCheck && stored.DownloadAllPar2 && stored.TemporaryFolderName);
        Assert.Equal(1, stored.MaxActiveDownloads);
        Assert.Equal(TimeSpan.FromMinutes(15), stored.PropagationDelay);
        Assert.Equal(DuplicateAction.Pause, stored.DuplicateAction);
        Assert.Equal(QueueSort.Newest, stored.AutoSort);
        Assert.Equal(".exe, .scr, .bat", stored.UnwantedExtensionsText);
        Assert.Equal(UnwantedAction.Warn, stored.UnwantedAction);
        Assert.Equal(EncryptedAction.Fail, stored.EncryptedAction);
        Assert.False(stored.OnlyVerified);
        Assert.Equal(".nfo, .txt", stored.CleanupListText);
        Assert.Equal("-t4", stored.Par2ArgumentsText);
        Assert.Equal("-mmt2 -bb1", stored.SevenZipArgumentsText);
        Assert.Equal(ToolPriority.Idle, stored.ToolPriority);
        Assert.Equal(2, stored.PostProcessWorkers);
        Assert.Equal(50_000, stored.MaxLineSpeedKBps);
        Assert.Equal((30, 500, false), (stored.HistoryKeepDays, stored.HistoryKeepItems, stored.HistoryKeepFailed));
        var pp = stored.PostProcess;
        Assert.False(pp.RecursiveUnpack || pp.IgnoreSamples);
        Assert.True(pp.Deobfuscate);
        Assert.Equal(["-mmt2", "-bb1"], pp.SevenZipArguments);

        Settings.Update(new DownloadSettingsChange { NzbBackupFolder = string.Empty, CleanupList = string.Empty });
        Assert.Null(Settings.NzbBackupFolder);
        Assert.Empty(Settings.PostProcess.CleanupExtensions);
    }

    [Fact]
    public void Invalid_values_are_refused_and_nothing_is_saved()
    {
        var error = Assert.Throws<ValidationFailedException>(() => Settings.Update(new DownloadSettingsChange
        {
            WatchIntervalSeconds = 0,
            MaxActiveDownloads = 101,
            PropagationDelayMinutes = -1,
            DuplicateAction = "maybe",
            AutoSort = "3",
            UnwantedExtensions = ".exe, ../x",
            CleanupList = "*.nfo",
            Par2Arguments = "-B/etc",
            SevenZipArguments = "-o/tmp",
            ToolPriority = "realtime",
            PostProcessWorkers = 9,
            MaxLineSpeedKBps = -5,
            HistoryKeepDays = -1,
            HistoryKeepItems = 1_000_000,
            NzbBackupFolder = "relative",
            PreCheck = true,
        }));

        Assert.Equal(
            [
                "autoSort", "duplicateAction", "historyKeepDays", "historyKeepItems", "maxActiveDownloads", "maxLineSpeedKBps", "nzbBackupFolder",
                "par2Arguments", "postProcessWorkers", "propagationDelayMinutes", "sevenZipArguments", "toolPriority", "unwantedExtensions", "watchIntervalSeconds",
            ],
            error.Errors.Keys.Order(StringComparer.Ordinal));
        Assert.False(Settings.PreCheck);
    }

    public void Dispose() => _bed.Dispose();
}
