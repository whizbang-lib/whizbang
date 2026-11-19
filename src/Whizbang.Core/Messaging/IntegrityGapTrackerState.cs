// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// #2104: an origin the gap tracker knows from its checkpoints, as the store keeps it across restarts.
/// </summary>
/// <param name="OriginServiceId">The origin's stable id.</param>
/// <param name="OriginServiceName">Its logical name, the target of directed integrity requests.</param>
/// <param name="RequestTopic">The address it consumes directed integrity requests on, when announced.</param>
/// <param name="LastCheckpointAt">When its last checkpoint was recorded: the liveness clock.</param>
/// <docs>resilience/stream-integrity#the-gap-tracker-survives-a-restart</docs>
public sealed record IntegrityKnownOrigin(
  Guid OriginServiceId, string OriginServiceName, string? RequestTopic, DateTimeOffset LastCheckpointAt);

/// <summary>
/// #2104: what the store keeps of <see cref="IntegrityGapTracker"/>: the known origins and the pending gaps awaiting
/// confirmation, loaded once by a new process (<see cref="IntegrityGapTracker.EnsureHydratedAsync"/>).
/// </summary>
/// <param name="Origins">Every origin a checkpoint was recorded from.</param>
/// <param name="Pending">Every deficit awaiting confirmation by its origin's next checkpoint.</param>
/// <docs>resilience/stream-integrity#the-gap-tracker-survives-a-restart</docs>
public sealed record IntegrityGapTrackerState(
  IReadOnlyList<IntegrityKnownOrigin> Origins, IReadOnlyList<IntegrityGapTracker.PendingGap> Pending);

/// <summary>
/// #2261: the ledger buckets the service opened and closed over a window, summed from the shared per-minute tally every
/// instance writes its own counts into.
/// </summary>
/// <param name="Opened">Buckets opened in the window.</param>
/// <param name="Closed">Buckets closed in the window.</param>
/// <docs>resilience/stream-integrity#derived-completion-meters</docs>
public readonly record struct IntegrityLedgerTally(long Opened, long Closed);
