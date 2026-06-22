namespace Whizbang.Sagas.Helpers;

/// <summary>
/// Outcome of a per-item stream check against the durable event store.
/// </summary>
public enum SagaItemTerminalOutcome {
  /// <summary>No terminal event exists on the per-item stream — the item is genuinely still in progress.</summary>
  NotTerminal = 0,

  /// <summary>A successful terminal event exists on the per-item stream.</summary>
  Completed = 1,

  /// <summary>A failed terminal event exists on the per-item stream.</summary>
  Failed = 2,
}

/// <summary>
/// Minimal abstraction over the durable event store used by
/// <see cref="SagaItemCompletionReconciler"/>. Implementations adapt
/// the consumer's <c>IEventStoreQuery</c> (Whizbang.Core) or any other
/// event-store API to answer the single question the reconciler asks:
/// "for this per-item stream, is there a terminal event, and if so,
/// completed or failed?"
/// </summary>
/// <remarks>
/// The narrow surface keeps the reconciler unit-testable in isolation
/// — implementations only have to satisfy this single method instead
/// of mocking a full IQueryable LINQ provider.
/// </remarks>
public interface ISagaItemTerminalReader {

  /// <summary>
  /// Returns the terminal outcome on <paramref name="perItemStreamId"/>,
  /// or <see cref="SagaItemTerminalOutcome.NotTerminal"/> if no terminal
  /// event exists.
  /// </summary>
  Task<SagaItemTerminalOutcome> CheckAsync(Guid perItemStreamId, CancellationToken cancellationToken);
}
