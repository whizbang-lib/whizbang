// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The single active standby request: a migrating instance asking live older peers to drain and
/// stand by before a breaking migration. Carries whether the requester is live by the one liveness
/// rule (#2027), so peers bound their wait by the requester's LIVENESS: a dead migrator's request is
/// void, and revival begins; every path out of standby is bounded.
/// </summary>
/// <param name="RequestedBy">The migrating instance.</param>
/// <param name="RequestedVersion">The version it intends to migrate to.</param>
/// <param name="RequestedAt">When it asked.</param>
/// <param name="RequesterIsLive">
/// Whether the database's one liveness rule (<c>wh_instance_is_live</c>) calls the requester live. A peer keeps no
/// window of its own: an instance with no row, or one the rule calls dead, voids its request.
/// </param>
/// <docs>operations/startup/rolling-upgrades#the-standby-handshake</docs>
public sealed record StandbyRequest(
  Guid RequestedBy, string RequestedVersion, DateTimeOffset RequestedAt, bool RequesterIsLive);
