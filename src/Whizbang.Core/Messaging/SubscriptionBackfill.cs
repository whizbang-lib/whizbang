// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// One (owner, event type) pair a grown subscription must backfill (#2092): what the subscription-expansion reconciler
/// queues, and the paced backfill then walks window by window.
/// </summary>
/// <param name="OwnerServiceId">The owner's service id, or <see cref="Guid.Empty"/> when no owner is known and each
/// window is broadcast.</param>
/// <param name="OwnerServiceName">The owner's service name, the target of each window; empty for a broadcast.</param>
/// <param name="EventType">The event type in the wire form the owner matches its store against.</param>
/// <docs>resilience/stream-integrity#paced-backfill</docs>
public sealed record SubscriptionBackfillSeed(Guid OwnerServiceId, string OwnerServiceName, string EventType);

/// <summary>How a paced backfill ended (#2092).</summary>
/// <docs>resilience/stream-integrity#paced-backfill</docs>
public enum SubscriptionBackfillOutcome {
  /// <summary>Still running.</summary>
  Running = 0,

  /// <summary>The owner answered a window with nothing more to send.</summary>
  Complete = 1,

  /// <summary>
  /// A window went unanswered for every allowed attempt. An owner on a release that predates windowed backfill answers
  /// the legacy way, with bundles but no window answer, so this is what a mixed-version fleet ends in; the integrity
  /// audit reports anything still missing.
  /// </summary>
  Unanswered = 2,
}

/// <summary>
/// The durable progress of one paced backfill (#2092): one row per (owner, event type), keyed by
/// <see cref="BackfillId"/>, which every window and its answer carry and whose session every bundle rides.
/// </summary>
/// <docs>resilience/stream-integrity#paced-backfill</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/SubscriptionBackfillCursorTests.cs</tests>
public sealed record SubscriptionBackfillCursor {
  /// <summary>The backfill's id: its windows' correlation and its bundles' session.</summary>
  public required Guid BackfillId { get; init; }

  /// <summary>The owner's service id, or <see cref="Guid.Empty"/> for a broadcast backfill.</summary>
  public required Guid OwnerServiceId { get; init; }

  /// <summary>The owner's service name; empty for a broadcast backfill.</summary>
  public required string OwnerServiceName { get; init; }

  /// <summary>The event type, in wire form.</summary>
  public required string EventType { get; init; }

  /// <summary>The last owner commit sequence received: the next window starts after it. Null before any event.</summary>
  public long? CursorCommitSequence { get; init; }

  /// <summary>The number of the window last requested (0 before the first).</summary>
  public long Window { get; init; }

  /// <summary>When the window last requested stops waiting for its answer; null when no answer is awaited.</summary>
  public DateTimeOffset? AnswerDueBy { get; init; }

  /// <summary>Times the current window has been requested without an answer.</summary>
  public int WindowAttempts { get; init; }

  /// <summary>Window requests sent, retries included.</summary>
  public long WindowsRequested { get; init; }

  /// <summary>Events the answered windows selected.</summary>
  public long EventsReceived { get; init; }

  /// <summary>The commit sequence of the first event received; null before any.</summary>
  public long? FirstCommitSequence { get; init; }

  /// <summary>The owner's highest commit sequence of the type at its last answer; null when unknown.</summary>
  public long? HeadCommitSequence { get; init; }

  /// <summary>When the backfill was queued.</summary>
  public DateTimeOffset StartedAt { get; init; }

  /// <summary>When a window was last requested; null before the first.</summary>
  public DateTimeOffset? LastRequestedAt { get; init; }

  /// <summary>When the backfill ended; null while it runs.</summary>
  public DateTimeOffset? CompletedAt { get; init; }

  /// <summary>How the backfill ended, or <see cref="SubscriptionBackfillOutcome.Running"/>.</summary>
  public SubscriptionBackfillOutcome Outcome { get; init; }

  /// <summary>
  /// The events estimated to remain: the density of what has arrived (events per commit sequence between the first and
  /// the current position) over the span still ahead (the owner's head less the current position). Zero once ended;
  /// null until a window has answered with events and a head.
  /// </summary>
  /// <returns>The estimate, or null when nothing yet supports one.</returns>
  public long? EstimateRemaining() {
    if (CompletedAt is not null) {
      return 0;
    }
    if (CursorCommitSequence is null) {
      return null;
    }
    if (FirstCommitSequence is null) {
      return null;
    }
    if (HeadCommitSequence is null) {
      return null;
    }
    var cursor = CursorCommitSequence.Value;
    var first = FirstCommitSequence.Value;
    var ahead = HeadCommitSequence.Value - cursor;
    if (ahead <= 0) {
      return 0;
    }
    var covered = cursor - first + 1;
    return (long)Math.Ceiling((double)EventsReceived * ahead / covered);
  }
}
