namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Context provided to the onDecisionMade callback when sync decision is made.
/// </summary>
/// <remarks>
/// <para>
/// This context is ALWAYS provided when a sync decision is made, regardless of outcome.
/// This is in contrast to <see cref="SyncWaitingContext"/> which is only provided when
/// actual waiting occurs.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync#callbacks</docs>
public sealed record SyncDecisionContext {
  /// <summary>
  /// The perspective type that was waited for, or null if waiting for all perspectives.
  /// </summary>
  public required Type? PerspectiveType { get; init; }

  /// <summary>
  /// The outcome of the sync operation.
  /// </summary>
  public required SyncOutcome Outcome { get; init; }

  /// <summary>
  /// The number of events that were awaited.
  /// </summary>
  public required int EventsAwaited { get; init; }

  /// <summary>
  /// The total elapsed time for the sync operation.
  /// </summary>
  public required TimeSpan ElapsedTime { get; init; }

  /// <summary>
  /// Whether actual waiting occurred. False for <see cref="SyncOutcome.NoPendingEvents"/>
  /// or when no awaiter was registered.
  /// </summary>
  public required bool DidWait { get; init; }
}
