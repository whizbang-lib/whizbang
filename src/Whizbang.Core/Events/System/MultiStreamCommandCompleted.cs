// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;

namespace Whizbang.Core.Events.System;

/// <summary>
/// A multi-stream command run whose every stream has reported. Published once per run, by the instance that recorded its
/// last outcome, in the run's own stream (the run id), never in one of the streams the run worked.
/// </summary>
/// <param name="StreamId">The run id: the run is its own stream.</param>
/// <param name="CommandType">The command's type name.</param>
/// <param name="StreamCount">How many streams the run named.</param>
/// <param name="Succeeded">How many succeeded.</param>
/// <param name="Failed">How many failed after their last attempt.</param>
/// <docs>fundamentals/dispatcher/multi-stream-commands#outcomes</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandWorkerTests.cs:RunOnceAsync_ARunThatCompleted_IsPublishedAndAnnouncedAsync</tests>
[PinnedId("31f2fa19-a55f-40bc-a6cc-314c2a2d0dc3")]
public record MultiStreamCommandCompleted(
    [property: StreamId] Guid StreamId,
    string CommandType,
    int StreamCount,
    int Succeeded,
    int Failed
) : IEvent;
