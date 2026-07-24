namespace Whizbang.Sagas;

/// <summary>
/// Emitted when a saga item fails. Carries error context so the saga
/// projection can surface the failure to UI and post-mortem tooling.
/// </summary>
public interface ISagaItemFailedEvent : ISagaItemEvent {

  /// <summary>Short human-readable summary of why the item failed.</summary>
  string ErrorMessage { get; }

  /// <summary>Optional structured details — typically a stack trace or stringified inner exception.</summary>
  string? ErrorDetails { get; }
}
