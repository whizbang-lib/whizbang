// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;

namespace Whizbang.Core.Events.System;

/// <summary>
/// A framework system event that is operational bookkeeping (#2320): the rewind and rebuild events. Each is written to a
/// stream of its own and nothing rebuilds state from it, so the profile on this interface makes every implementer
/// ephemeral, reaped <see cref="SystemEventRetention.DEFAULT_TTL_SECONDS"/> after it is written unless
/// <c>Whizbang:Ephemeral:SystemEvents:Ttl</c> says otherwise. An event written into an application stream must not
/// implement it: a stream never holds both ephemeral and Sourced events (WHIZ133).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#framework-system-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Events/System/SystemEventRetentionTests.cs:EveryRewindAndRebuildEvent_IsEphemeral_ForThirtyDaysByDefaultAsync</tests>
[Ephemeral(
  Destruction = Destruction.AfterTtl,
  TtlSeconds = SystemEventRetention.DEFAULT_TTL_SECONDS,
  RewindGraceSeconds = SystemEventRetention.DEFAULT_TTL_SECONDS)]
public interface IEphemeralSystemEvent : IEvent;
