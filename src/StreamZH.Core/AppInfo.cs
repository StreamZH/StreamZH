// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Reflection;

namespace StreamZH.Core;

public static class AppInfo
{
    public const string Name = "StreamZH";

    /// <summary>
    /// The GitHub repository (owner/name): releases for the update check and links to the documentation. The one place
    /// to change when the repository moves (the release workflow derives the image name from the repository owner).
    /// </summary>
    public const string Repository = "streamzh/streamzh";

    public const string RepositoryUrl = "https://github.com/" + Repository;

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}