// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.SignalR;

namespace StreamZH.Host.Hubs;

/// <summary>Pushes queue progress, pipeline events and notifications to the web UI.</summary>
public sealed class EventsHub : Hub;