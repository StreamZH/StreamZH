// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Pipeline;

/// <summary>Stages every job walks through, regardless of media type.</summary>
public enum PipelineStage
{
    Search = 1,
    Grab = 2,
    Download = 3,
    VerifyAndUnpack = 4,
    ImportAndRename = 5,
    Notify = 6,
    Completed = 7,
    Failed = 99,
}