namespace Whizbang.Sagas;

/// <summary>
/// Coarse-grained lifecycle status of a saga as a whole. Per-item status
/// is tracked separately by <see cref="SagaItemState"/>.
/// </summary>
/// <remarks>
/// <para>
/// Sagas advance through these values monotonically except for
/// <see cref="Reset"/>, which is a transition marker rather than a
/// resting state — a saga in <see cref="Reset"/> immediately becomes
/// <see cref="Running"/> again on the next dispatched item event.
/// </para>
/// </remarks>
public enum SagaStatus {
  /// <summary>Saga has been initiated but no items have started yet.</summary>
  Pending = 0,

  /// <summary>At least one item has started; the saga is in flight.</summary>
  Running = 1,

  /// <summary>All items terminal and all of them succeeded.</summary>
  Completed = 2,

  /// <summary>All items terminal; at least one failed but the saga ran to the end.</summary>
  CompletedWithFailures = 3,

  /// <summary>Saga aborted (typically fail-fast) before all items finished.</summary>
  Failed = 4,

  /// <summary>Transient marker after a <c>SagaResetEvent</c>; not a resting state.</summary>
  Reset = 5,
}
