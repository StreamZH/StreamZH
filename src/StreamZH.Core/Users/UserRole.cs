// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Users;

/// <summary>Same roles as CloudZH.</summary>
public enum UserRole
{
    /// <summary>Created at first start. Full access, cannot be removed.</summary>
    Owner = 1,

    /// <summary>Full configuration access.</summary>
    Admin = 2,

    /// <summary>May add and delete media, no configuration.</summary>
    Friend = 3,
}