namespace Whizbang.Sagas;

/// <summary>
/// Emitted to re-attempt a specific item that previously reached a
/// terminal failed state. Moves the item back from
/// <see cref="SagaItemState.Failed"/> to <see cref="SagaItemState.Pending"/>
/// and re-opens the saga's lifecycle.
/// </summary>
public interface ISagaResetEvent : ISagaEvent {

  /// <summary>Identifier of the item being reset.</summary>
  string ItemIdentifier { get; }

  /// <summary>Status the item held prior to reset (audit aid).</summary>
  SagaItemState PreviousStatus { get; }
}
