namespace Whizbang.Sagas;

/// <summary>
/// Terminal event for a saga. Carries the final status and the snapshot
/// of item-completion counts at completion time. Emitted exactly once
/// per saga via the dispatcher's <c>PublishOnceAsync</c> primitive — the
/// race that would otherwise produce 1–4× duplicates under N concurrent
/// terminal handlers is closed at the dispatcher.
/// </summary>
public interface ISagaCompletedEvent : ISagaEvent {

  /// <summary>Final lifecycle status: <see cref="SagaStatus.Completed"/>, <see cref="SagaStatus.CompletedWithFailures"/>, or <see cref="SagaStatus.Failed"/>.</summary>
  SagaStatus FinalStatus { get; }

  /// <summary>Item identifier whose terminal event triggered the saga's completion. Audit-only; helps trace which item "ended" the saga.</summary>
  string? CompletedByItemIdentifier { get; }

  /// <summary>Number of items in <see cref="SagaItemState.Completed"/> at completion time.</summary>
  int CompletedItems { get; }

  /// <summary>Number of items in <see cref="SagaItemState.Failed"/> at completion time.</summary>
  int FailedItems { get; }

  /// <summary>Total item count at completion time (may differ from initiated count if items were added/removed mid-saga).</summary>
  int TotalItems { get; }
}
