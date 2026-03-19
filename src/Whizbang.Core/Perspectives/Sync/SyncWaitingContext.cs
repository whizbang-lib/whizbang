namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Context provided to the onWaiting callback when sync waiting begins.
/// </summary>
/// <remarks>
/// <para>
/// This context is only provided when actual waiting is about to occur.
/// It is NOT provided for <see cref="SyncOutcome.NoPendingEvents"/> outcomes.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync#callbacks</docs>
public sealed record SyncWaitingContext {
  /// <summary>
  /// The perspective type being waited for, or null if waiting for all perspectives.
  /// </summary>
  public required Type? PerspectiveType { get; init; }

  /// <summary>
  /// The number of events being waited for.
  /// </summary>
  public required int EventCount { get; init; }

  /// <summary>
  /// The stream IDs of the events being waited for.
  /// </summary>
  public required IReadOnlyList<Guid> StreamIds { get; init; }

  /// <summary>
  /// The configured timeout for this wait operation.
  /// </summary>
  public required TimeSpan Timeout { get; init; }

  /// <summary>
  /// The UTC timestamp when waiting started.
  /// </summary>
  public required DateTimeOffset StartedAt { get; init; }
}
