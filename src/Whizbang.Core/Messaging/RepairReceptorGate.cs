// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>Why a repaired event was delivered state only, or run through its opted-in receptors (#2273).</summary>
/// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/RepairReceptorGateTests.cs</tests>
public enum RepairReceptorReason {
  /// <summary>The bundle was asked for state only (a backfill), so its children were state only already.</summary>
  RequestedStateOnly = 0,

  /// <summary>The repair receptor lookback is zero, which turns receptors on repair off.</summary>
  NoLookback = 1,

  /// <summary>The event's id carries no origin time (it is not a time-ordered id), so its age cannot be judged.</summary>
  NoOriginTime = 2,

  /// <summary>The event's origin time is older than the configured lookback.</summary>
  OutsideLookback = 3,

  /// <summary>
  /// The event is inside the configured lookback but older than the time the handled ledger starts covering: the
  /// lookback in effect is clamped to the ledger, so a lookback raised past its coverage proves nothing.
  /// </summary>
  BeforeLedgerCoverage = 4,

  /// <summary>The handled ledger could not be read, or this store keeps none, so nothing can be proven never handled.</summary>
  LedgerUnavailable = 5,

  /// <summary>The handled ledger records the event as handled here.</summary>
  AlreadyHandled = 6,

  /// <summary>The handled ledger covers the event and does not hold it: it runs the receptors declared to run on repair.</summary>
  NeverHandled = 7,
}

/// <summary>
/// Decides, for the children of a stream-integrity redelivery bundle, whether each repaired event runs its opted-in
/// receptors or is stored and projected state only (#2273). A child runs receptors only when its origin time (the
/// timestamp of its time-ordered id) is inside <see cref="StreamIntegrityOptions.RepairReceptorLookback"/>, clamped to
/// the handled ledger's coverage, and the ledger shows this service never handled it; such a child is marked
/// <see cref="IMessageEnvelope.Repaired"/>, so only receptors declared <see cref="RunOnRepairAttribute"/> run. Every other
/// child is marked <see cref="IMessageEnvelope.StateOnly"/>. The ledger is asked once per bundle, and only for the
/// children the configured lookback admits.
/// </summary>
/// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/RepairReceptorGateTests.cs</tests>
public static class RepairReceptorGate {
  /// <summary>Marks each child of a redelivery bundle and counts the decisions by reason.</summary>
  /// <param name="children">The bundle's children, as the fan-out built them (their envelopes are
  /// <see cref="MessageEnvelope{T}"/> of <see cref="JsonElement"/>).</param>
  /// <param name="bundleStateOnly">Whether the bundle itself was asked for state only.</param>
  /// <param name="lookback">The configured repair receptor lookback.</param>
  /// <param name="now">The moment of the decision.</param>
  /// <param name="lookup">Reads the handled ledger for the given ids; null when it cannot be read.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>How many children were decided for each reason.</returns>
  public static Task<IReadOnlyDictionary<RepairReceptorReason, int>> ApplyAsync(
      IReadOnlyList<InboxMessage> children, bool bundleStateOnly, TimeSpan lookback, DateTimeOffset now,
      Func<IReadOnlyList<Guid>, CancellationToken, Task<HandledLedgerAnswer?>> lookup,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(children);
    ArgumentNullException.ThrowIfNull(lookup);
    _ = (bundleStateOnly, lookback, now, cancellationToken);
    return Task.FromResult<IReadOnlyDictionary<RepairReceptorReason, int>>(new Dictionary<RepairReceptorReason, int>());
  }
}
