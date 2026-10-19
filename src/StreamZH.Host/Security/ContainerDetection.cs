// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Host.Service;

namespace StreamZH.Host.Security;

/// <summary>
/// StreamZH runs in a container (Docker, Podman, Kubernetes, LXC). Matters for "Disabled for local addresses": with port
/// publishing (NAT, Docker Desktop, rootless Docker/Podman) every visitor may reach StreamZH from a private address of the
/// container network, so everyone would count as local (security review S1-06).
/// </summary>
public static class ContainerDetection
{
    private static readonly Lazy<bool> Detected = new(Detect);

    public static bool IsContainer => Detected.Value;

    private static bool Detect()
    {
        if (RunningAs.Mode == RunningAs.Docker || !OperatingSystem.IsLinux())
        {
            return RunningAs.Mode == RunningAs.Docker;
        }

        try
        {
            if (File.Exists("/.dockerenv") || File.Exists("/run/.containerenv"))
            {
                return true;
            }

            return File.Exists("/proc/1/cgroup") && IsContainerCgroup(File.ReadAllText("/proc/1/cgroup"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsContainerCgroup(string cgroup)
    {
        ArgumentNullException.ThrowIfNull(cgroup);
        return cgroup.Contains("docker", StringComparison.Ordinal)
            || cgroup.Contains("kubepods", StringComparison.Ordinal)
            || cgroup.Contains("containerd", StringComparison.Ordinal)
            || cgroup.Contains("libpod", StringComparison.Ordinal)
            || cgroup.Contains("/lxc/", StringComparison.Ordinal);
    }
}
