namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Where an event stands for one perspective, as the applied-event ledger records it.
/// </summary>
/// <docs>fundamentals/perspectives/perspective-sync#applied-ledger</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/AppliedEventStatusTests.cs</tests>
public enum AppliedEventState {
  /// <summary>The event is not in the local event store yet (an inbound event is stored when its inbox row is claimed).</summary>
  NotArrived = 0,

  /// <summary>The event is stored and work to apply it to the perspective (or the collective sink) is outstanding.</summary>
  Pending = 1,

  /// <summary>The perspective, or the collective sink, recorded the event as applied when its apply committed.</summary>
  Applied = 2,

  /// <summary>
  /// The event is stored, has no applied record and no outstanding work: the perspective does not handle it,
  /// or its record has been pruned. Nothing is left to wait for.
  /// </summary>
  NotApplicable = 3,
}

/// <summary>
/// What a wait for an applied event asks: one perspective, and the event by its id or by its stream and
/// position in this service's event store.
/// </summary>
/// <param name="PerspectiveName">The perspective's name, as the work rows and the ledger store it.</param>
/// <param name="EventId">The event id, or <see langword="null"/> to name the event by stream and position.</param>
/// <param name="StreamId">The stream, when the event is named by position.</param>
/// <param name="StreamPosition">The event's per-stream version in the local event store (1 for the first event).</param>
/// <docs>fundamentals/perspectives/perspective-sync#cross-service</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/AppliedEventStatusTests.cs</tests>
public sealed record AppliedEventInquiry(
    string PerspectiveName,
    Guid? EventId,
    Guid? StreamId = null,
    int? StreamPosition = null);

/// <summary>
/// The ledger's answer to an <see cref="AppliedEventInquiry"/>.
/// </summary>
/// <param name="State">Where the event stands for the perspective.</param>
/// <param name="EventId">The event's id, resolved from the stream and position when the inquiry named it that way.</param>
/// <docs>fundamentals/perspectives/perspective-sync#applied-ledger</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/AppliedEventStatusTests.cs</tests>
public readonly record struct AppliedEventStatus(AppliedEventState State, Guid? EventId) {
  /// <summary>True when there is nothing left to wait for: the event was applied, or the perspective has nothing to apply.</summary>
  public bool IsSettled => State is AppliedEventState.Applied or AppliedEventState.NotApplicable;
}
