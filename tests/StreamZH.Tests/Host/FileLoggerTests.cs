// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using StreamZH.Host.Logging;

namespace StreamZH.Tests.Host;

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"streamzh-logs-{Guid.NewGuid():N}");

    [Fact]
    public void Respects_level_switch_and_rolls_over()
    {
        var levelSwitch = new LogLevelSwitch(LogLevel.Information);
        using var provider = new FileLoggerProvider(_directory, levelSwitch);
        var logger = provider.CreateLogger("StreamZH.Usenet.Downloader");

        logger.LogDebug("hidden");
        logger.LogInformation("visible");
        levelSwitch.Minimum = LogLevel.Debug;
        logger.LogDebug("now visible");

        var tail = provider.Tail(10);
        Assert.Equal(2, tail.Count);
        Assert.EndsWith("|Info|Downloader|visible", tail[0], StringComparison.Ordinal);
        Assert.EndsWith("|Debug|Downloader|now visible", tail[1], StringComparison.Ordinal);

        var filler = new string('x', 1000);
        for (var i = 0; i < 1200; i++)
        {
            logger.LogInformation("{Filler}", filler);
        }

        Assert.True(File.Exists(Path.Combine(_directory, "streamzh.1.txt")));
        Assert.True(new FileInfo(provider.CurrentFile).Length < 1024 * 1024);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}