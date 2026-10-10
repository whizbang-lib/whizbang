// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The single active standby request: a migrating instance asking live older peers to drain and
/// stand by before a breaking migration. Carries the requester's last heartbeat so peers bound
/// their wait by the requester's LIVENESS — a dead migrator's request is void, and revival
/// begins; every path out of standby is bounded.
/// </summary>
/// <param name="RequestedBy">The migrating instance.</param>
/// <param name="RequestedVersion">The version it intends to migrate to.</param>
/// <param name="RequestedAt">When it asked.</param>
/// <param name="RequesterLastHeartbeatAt">
/// The requester's last heartbeat, or null when its instance row is gone (reaped) — either way,
/// a peer treats a silent requester as dead and the request as void.
/// </param>
/// <param name="RequesterAliveLockHeld">
/// Whether the requester's session alive-lock is held right now (#1286). A requester holding it is alive
/// whatever the age of <paramref name="RequesterLastHeartbeatAt"/>: it beats on the slow cadence.
/// </param>
/// <docs>operations/startup/rolling-upgrades#the-standby-handshake</docs>
public sealed record StandbyRequest(
  Guid RequestedBy, string RequestedVersion, DateTimeOffset RequestedAt,
  DateTimeOffset? RequesterLastHeartbeatAt, bool RequesterAliveLockHeld = false);
