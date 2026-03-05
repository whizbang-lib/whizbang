namespace Whizbang.Core.Perspectives;

/// <summary>
/// Base marker interface for multi-stream (global) perspectives.
/// Multi-stream perspectives aggregate events from multiple streams based on a partition key.
/// Inspired by Marten's MultiStreamProjection pattern with Identity() method.
/// </summary>
/// <typeparam name="TModel">The read model type that this perspective maintains</typeparam>
/// <typeparam name="TPartitionKey">The type of partition key (Guid, string, int, etc.)</typeparam>
/// <docs>core-concepts/perspectives/multi-stream</docs>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull {
  // Marker interface - no methods required
  // Specific event handling enforced by IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent> variants
}

/// <summary>
/// Multi-stream perspective that handles a single event type with partition key extraction.
/// GetPartitionKey extracts the partition from events (like Marten's Identity method).
/// Apply methods must be pure functions: no I/O, no side effects, deterministic.
/// </summary>
/// <typeparam name="TModel">The read model type</typeparam>
/// <typeparam name="TPartitionKey">The partition key type (Guid, string, int, etc.)</typeparam>
/// <typeparam name="TEvent1">The event type this perspective handles</typeparam>
/// <docs>core-concepts/perspectives/multi-stream</docs>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent {
  /// <summary>
  /// Extracts the partition key from an event to determine which model instance to update.
  /// MUST be a pure function: deterministic, no side effects.
  /// </summary>
  /// <param name="eventData">The event that occurred</param>
  /// <returns>The partition key that identifies which model to update</returns>
  TPartitionKey GetPartitionKey(TEvent1 eventData);

  /// <summary>
  /// Applies an event to the model and returns a new model.
  /// MUST be a pure function: no I/O, no side effects, deterministic.
  /// Use event timestamps instead of DateTime.UtcNow for time values.
  /// </summary>
  /// <param name="currentData">The current state of the read model for this partition</param>
  /// <param name="eventData">The event that occurred</param>
  /// <returns>A new model with the event applied</returns>
  TModel Apply(TModel currentData, TEvent1 eventData);
}

/// <summary>
/// Multi-stream perspective that handles two event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent {
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  TPartitionKey GetPartitionKey(TEvent2 eventData);

  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
}

/// <summary>
/// Multi-stream perspective that handles three event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent {
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  TPartitionKey GetPartitionKey(TEvent3 eventData);

  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
}

// FUTURE: Generate remaining variants (4-50 event types) via source generator or T4 template
// For now, we have enough to validate the pattern works
