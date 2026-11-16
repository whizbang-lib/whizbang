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
  public static async Task<IReadOnlyDictionary<RepairReceptorReason, int>> ApplyAsync(
      IReadOnlyList<InboxMessage> children, bool bundleStateOnly, TimeSpan lookback, DateTimeOffset now,
      Func<IReadOnlyList<Guid>, CancellationToken, Task<HandledLedgerAnswer?>> lookup,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(children);
    ArgumentNullException.ThrowIfNull(lookup);
    var tally = new Dictionary<RepairReceptorReason, int>();
    // A lookback reaching past the calendar's start admits every timed event rather than overflowing.
    var configuredFloor = lookback >= now - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : now - lookback;
    var candidates = new List<(MessageEnvelope<JsonElement> Envelope, Guid Id, DateTimeOffset Origin)>(children.Count);
    foreach (var child in children) {
      // The fan-out builds every child envelope as a MessageEnvelope<JsonElement>; the markers are its settable fields.
      var envelope = (MessageEnvelope<JsonElement>)child.Envelope;
      var origin = TrackedGuid.FromExternal(child.MessageId).Timestamp;
      if (_decidedWithoutTheLedger(bundleStateOnly, lookback, origin, configuredFloor) is { } reason) {
        _decide(envelope, reason, tally);
      } else {
        candidates.Add((envelope, child.MessageId, origin));
      }
    }
    if (candidates.Count == 0) {
      return tally;
    }

    var ids = new Guid[candidates.Count];
    for (var i = 0; i < ids.Length; i++) {
      ids[i] = candidates[i].Id;
    }
    var answer = await lookup(ids, cancellationToken).ConfigureAwait(false);
    foreach (var (envelope, id, origin) in candidates) {
      _decide(envelope, _ledgerReason(answer, id, origin), tally);
    }
    return tally;
  }

  // The decisions the ledger has no say in: a state-only bundle, receptors on repair turned off, an event whose age is
  // unknown or older than the configured lookback. Null when the ledger must be asked.
  private static RepairReceptorReason? _decidedWithoutTheLedger(
      bool bundleStateOnly, TimeSpan lookback, DateTimeOffset origin, DateTimeOffset configuredFloor) {
    if (bundleStateOnly) {
      return RepairReceptorReason.RequestedStateOnly;
    }
    if (lookback <= TimeSpan.Zero) {
      return RepairReceptorReason.NoLookback;
    }
    if (origin == DateTimeOffset.MinValue) {
      return RepairReceptorReason.NoOriginTime;
    }
    return origin < configuredFloor ? RepairReceptorReason.OutsideLookback : null;
  }

  // What the ledger says about one event the configured lookback admits. Coverage clamps the lookback: an event older
  // than the time the ledger starts covering may have been handled before any record was kept.
  private static RepairReceptorReason _ledgerReason(HandledLedgerAnswer? answer, Guid id, DateTimeOffset origin) {
    if (answer?.CoversFrom is not { } coversFrom) {
      return RepairReceptorReason.LedgerUnavailable;
    }
    if (origin < coversFrom) {
      return RepairReceptorReason.BeforeLedgerCoverage;
    }
    return answer.Handled.Contains(id) ? RepairReceptorReason.AlreadyHandled : RepairReceptorReason.NeverHandled;
  }

  private static void _decide(MessageEnvelope<JsonElement> envelope, RepairReceptorReason reason, Dictionary<RepairReceptorReason, int> tally) {
    if (reason == RepairReceptorReason.NeverHandled) {
      envelope.Repaired = true;
    } else {
      envelope.StateOnly = true;
    }
    tally[reason] = tally.GetValueOrDefault(reason) + 1;
  }
}
