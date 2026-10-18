// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Profiles;

namespace StreamZH.Library;

/// <summary>Told by <see cref="QualityProfileService"/> when the user changes or deletes a profile (e.g. the TRaSH sync keeps the user's scores).</summary>
public interface IQualityProfileObserver
{
    void Updated(QualityProfile before, QualityProfile after);

    void Deleted(int profileId);
}
