namespace Whizbang.Sagas;

/// <summary>
/// Emitted after a lifecycle hook finishes. Carries the hook's final
/// status (succeeded or failed) and optional error context.
/// </summary>
public interface ISagaHookCompletedEvent : ISagaHookEvent {

  /// <summary>Final hook status — <see cref="SagaItemState.Completed"/> or <see cref="SagaItemState.Failed"/>.</summary>
  SagaItemState Status { get; }

  /// <summary>Error message if the hook failed.</summary>
  string? ErrorMessage { get; }

  /// <summary>Optional structured error details.</summary>
  string? ErrorDetails { get; }
}
