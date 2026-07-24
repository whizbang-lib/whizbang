namespace Whizbang.Sagas;

/// <summary>
/// State of a single saga item. Each item moves
/// <c>Pending → Running → (Completed | Failed | Skipped)</c>; the three
/// terminal states are equivalent for the purpose of saga completion
/// counting (none of them blocks the parent saga from completing).
/// </summary>
public enum SagaItemState {
  /// <summary>Item exists on the saga but has not been dispatched.</summary>
  Pending = 0,

  /// <summary>Item is being processed by its handler.</summary>
  Running = 1,

  /// <summary>Item finished successfully.</summary>
  Completed = 2,

  /// <summary>Item finished with a domain error.</summary>
  Failed = 3,

  /// <summary>Item was intentionally skipped (filtered out, no-op, already-applied).</summary>
  Skipped = 4,
}
