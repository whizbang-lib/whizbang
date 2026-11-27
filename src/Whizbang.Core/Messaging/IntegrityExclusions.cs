// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// An operator's exclusion of received events from integrity checks (#2588): an escape hatch, never a repair. A mark
/// stops the integrity engine from comparing, asking about, holding, healing or alarming on the events, on both sides of
/// every comparison, until it is reversed. Each mark records a reason, who asked and when, and is logged.
/// </summary>
/// <remarks>
/// <para>
/// Only received history can be excluded: an event this service wrote is refused, because its consumers compare against
/// what it answers (decision D1). An event this service does not store is refused too; excluding events a service is
/// missing is open question #2616. Use it when a defect makes events unresolvable until a fix ships, or to draw a line
/// under old history recorded under earlier rules; never when a fix is possible.
/// </para>
/// <para>
/// Marking changes what the eligibility rule counts (version 2), so a pair of services on different rule versions halts
/// its comparisons until both upgrade (decision D2, open question #2615).
/// </para>
/// </remarks>
/// <docs>resilience/integrity-exclusions</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionsApiTests.cs</tests>
public interface IIntegrityExclusions {
  /// <summary>
  /// Excludes events from integrity checks, by ids or by selection. Up to the inline limit of ids are marked at once;
  /// more ids, and every selection, are queued and applied by the maintenance step in bounded batches. A dry run counts
  /// what would be marked and marks nothing.
  /// </summary>
  /// <param name="request">What to exclude and why.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>What was marked (or would be), or the queued request's id.</returns>
  /// <exception cref="ArgumentException">The request is invalid: no reason, nobody named, neither or both of ids and a
  /// selection, or a selection naming this service as the origin.</exception>
  Task<IntegrityExclusionResult> ExcludeAsync(IntegrityExclusionRequest request, CancellationToken cancellationToken = default);

  /// <summary>
  /// Reverses exclusions, by ids, by the request that made them, or by origin and types; logged like a mark. The events
  /// are then resolved again from scratch.
  /// </summary>
  /// <param name="request">What to reverse and why.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>What was unmarked, or the queued request's id.</returns>
  /// <exception cref="ArgumentException">The request is invalid.</exception>
  Task<IntegrityExclusionResult> UnexcludeAsync(IntegrityUnexclusionRequest request, CancellationToken cancellationToken = default);

  /// <summary>Per origin, the exclusions this service holds and how many are still pending an answer from the origin.</summary>
  /// <param name="originServiceId">One origin, or null for every origin.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The summary.</returns>
  Task<IntegrityExclusionSummary> SummarizeAsync(Guid? originServiceId = null, CancellationToken cancellationToken = default);
}

/// <summary>A request to exclude events from integrity checks (#2588).</summary>
/// <docs>resilience/integrity-exclusions#the-api</docs>
public sealed record IntegrityExclusionRequest {
  /// <summary>Why: required, recorded on every mark and in the log.</summary>
  public required string Reason { get; init; }

  /// <summary>Who asked: required, recorded on every mark and in the log.</summary>
  public required string RequestedBy { get; init; }

  /// <summary>The events, by id. Either this or <see cref="Selection"/>, never both.</summary>
  public IReadOnlyList<Guid>? EventIds { get; init; }

  /// <summary>The events, by selection. Either this or <see cref="EventIds"/>, never both.</summary>
  public IntegrityExclusionSelection? Selection { get; init; }

  /// <summary>Count what would be marked, and mark nothing.</summary>
  public bool DryRun { get; init; }
}

/// <summary>
/// Which received events to exclude (#2588). Every condition given must hold. With <see cref="Unresolved"/>, the events
/// are those the engine currently cannot resolve, before <see cref="Until"/>.
/// </summary>
/// <docs>resilience/integrity-exclusions#selections</docs>
public sealed record IntegrityExclusionSelection {
  /// <summary>Only events received from this origin; null for every origin.</summary>
  public Guid? OriginServiceId { get; init; }

  /// <summary>Only events of these stored type names; null for every type.</summary>
  public IReadOnlyList<string>? EventTypes { get; init; }

  /// <summary>Only events stored at or after this time.</summary>
  public DateTimeOffset? From { get; init; }

  /// <summary>Only events stored before this time (with <see cref="Unresolved"/>: recorded as unresolved before it).</summary>
  public DateTimeOffset? Until { get; init; }

  /// <summary>Only what is currently unresolved, of the kinds given; <see cref="IntegrityUnresolved.None"/> for any event.</summary>
  public IntegrityUnresolved Unresolved { get; init; }
}

/// <summary>What the integrity engine currently cannot resolve, as a selection names it (#2588).</summary>
/// <docs>resilience/integrity-exclusions#the-past-everything-unresolved-before-a-time</docs>
[Flags]
public enum IntegrityUnresolved {
  /// <summary>No condition: every event the rest of the selection names.</summary>
  None = 0,

  /// <summary>Events whose origin answered that it does not know them.</summary>
  UnknownAnswers = 1,

  /// <summary>Events held without their origin's commit sequence, which leave their windows undecided.</summary>
  Undecided = 2,

  /// <summary>
  /// The stored events of repair-ledger stream buckets marked unhealable or exhausted. The events such a bucket is
  /// missing are not stored here and stay counted (open question #2616).
  /// </summary>
  Unhealed = 4,

  /// <summary>All of the above.</summary>
  All = UnknownAnswers | Undecided | Unhealed,
}

/// <summary>A request to reverse exclusions (#2588). Exactly one of the three ways to name them.</summary>
/// <docs>resilience/integrity-exclusions#the-api</docs>
public sealed record IntegrityUnexclusionRequest {
  /// <summary>Why: required, recorded in the log.</summary>
  public required string Reason { get; init; }

  /// <summary>Who asked: required, recorded in the log.</summary>
  public required string RequestedBy { get; init; }

  /// <summary>The marks, by event id.</summary>
  public IReadOnlyList<Guid>? EventIds { get; init; }

  /// <summary>Every mark the request with this id made (<see cref="IntegrityExclusionResult.RequestId"/>).</summary>
  public Guid? RequestId { get; init; }

  /// <summary>Every mark of this origin (optionally only of <see cref="EventTypes"/>).</summary>
  public Guid? OriginServiceId { get; init; }

  /// <summary>With <see cref="OriginServiceId"/>: only marks of these types.</summary>
  public IReadOnlyList<string>? EventTypes { get; init; }
}

/// <summary>How many events of one origin had one outcome (#2588).</summary>
/// <param name="OriginServiceId">The origin; null for events with none (this service's own, or not stored).</param>
/// <param name="Outcome">
/// <c>marked</c> or <c>unmarked</c>; or why not: <c>own_event</c> (this service wrote it), <c>written_here</c> (its first
/// hop names this service), <c>not_stored</c> (#2616), <c>superseding</c>, <c>already_excluded</c>, <c>not_excluded</c>.
/// </param>
/// <param name="Count">How many.</param>
/// <docs>resilience/integrity-exclusions#the-api</docs>
public sealed record IntegrityExclusionOutcome(Guid? OriginServiceId, string Outcome, int Count);

/// <summary>What an exclude or unexclude call did (#2588).</summary>
/// <docs>resilience/integrity-exclusions#the-api</docs>
public sealed record IntegrityExclusionResult {
  /// <summary>The queued request's id, when the call was queued for the maintenance step; null when applied at once.</summary>
  public Guid? RequestId { get; init; }

  /// <summary>True when the call was queued: the outcomes are then empty, and the log says what each batch did.</summary>
  public bool Queued { get; init; }

  /// <summary>True for a dry run: the outcomes say what would happen, and nothing was marked.</summary>
  public bool DryRun { get; init; }

  /// <summary>For a dry run, true when the count stopped at the cap.</summary>
  public bool Capped { get; init; }

  /// <summary>Per origin and outcome, how many events.</summary>
  public IReadOnlyList<IntegrityExclusionOutcome> Outcomes { get; init; } = [];
}

/// <summary>One origin's exclusions (#2588).</summary>
/// <param name="OriginServiceId">The origin.</param>
/// <param name="Excluded">Every mark of the origin.</param>
/// <param name="Pending">Marks whose place at the origin is not known yet; their buckets are undecided.</param>
/// <param name="UnknownToOrigin">Marks of events the origin does not know.</param>
/// <param name="LastMarkedAt">When the last mark was made.</param>
/// <docs>resilience/integrity-exclusions#the-summary</docs>
public sealed record IntegrityExclusionOriginSummary(
  Guid OriginServiceId, long Excluded, long Pending, long UnknownToOrigin, DateTimeOffset? LastMarkedAt);

/// <summary>The exclusions this service holds (#2588).</summary>
/// <docs>resilience/integrity-exclusions#the-summary</docs>
public sealed record IntegrityExclusionSummary {
  /// <summary>Per origin with any mark.</summary>
  public IReadOnlyList<IntegrityExclusionOriginSummary> Origins { get; init; } = [];

  /// <summary>Requests queued and not finished.</summary>
  public long OpenRequests { get; init; }
}

/// <summary>Integrity exclusions (<c>Whizbang:StreamIntegrity:Exclusions</c>, #2588).</summary>
/// <docs>resilience/integrity-exclusions#configuration</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/IntegrityExclusionOptionsTests.cs</tests>
public sealed class IntegrityExclusionOptions {
  /// <summary>Events one maintenance batch marks or unmarks (<c>...:Exclusions:BatchSize</c>, default 1000). At least 1.</summary>
  public int BatchSize { get; set; } = 1000;

  /// <summary>
  /// The most event ids a call marks at once (<c>...:Exclusions:InlineLimit</c>, default 100); a call naming more is
  /// queued for the maintenance step. 0 queues every call.
  /// </summary>
  public int InlineLimit { get; set; } = 100;

  /// <summary>
  /// How long the audit log and finished requests are kept (<c>...:Exclusions:LogRetention</c>, default 365 days). Must
  /// be positive.
  /// </summary>
  public TimeSpan LogRetention { get; set; } = TimeSpan.FromDays(365);
}
