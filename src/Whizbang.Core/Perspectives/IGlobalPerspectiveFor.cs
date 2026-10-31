// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

#pragma warning disable S2326 // Unused type parameters should be removed
#pragma warning disable S2436 // Reduce the number of type parameters in the generic type
// TModel, TPartitionKey, and TEvent1-TEvent50 are intentionally declared at the interface level
// to document which model and event types are valid. The runtime implementation enforces type constraints.
// Up to 50 event types, the same as IPerspectiveFor and IPerspectiveWithActionsFor.

/// <summary>
/// Base marker interface for multi-stream (global) perspectives.
/// Multi-stream perspectives aggregate events from multiple streams based on a partition key.
/// Inspired by Marten's MultiStreamProjection pattern with Identity() method.
/// </summary>
/// <typeparam name="TModel">The read model type that this perspective maintains</typeparam>
/// <typeparam name="TPartitionKey">The type of partition key (Guid, string, int, etc.)</typeparam>
/// <docs>fundamentals/perspectives/multi-stream</docs>
/// <tests>tests/Whizbang.Generators.Tests/GlobalPerspectiveArityTests.cs:TheGlobalFamily_TakesUpToFiftyEvents_AndNoMoreAsync</tests>
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
/// <docs>fundamentals/perspectives/multi-stream</docs>
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
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
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
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
}

/// <summary>
/// Multi-stream perspective that handles four event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
}

/// <summary>
/// Multi-stream perspective that handles five event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
}

/// <summary>
/// Multi-stream perspective that handles six event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
}

/// <summary>
/// Multi-stream perspective that handles seven event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
}

/// <summary>
/// Multi-stream perspective that handles eight event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
}

/// <summary>
/// Multi-stream perspective that handles nine event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
}

/// <summary>
/// Multi-stream perspective that handles ten event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
}

/// <summary>
/// Multi-stream perspective that handles eleven event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
}

/// <summary>
/// Multi-stream perspective that handles twelve event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
}

/// <summary>
/// Multi-stream perspective that handles thirteen event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
}

/// <summary>
/// Multi-stream perspective that handles fourteen event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
}

/// <summary>
/// Multi-stream perspective that handles fifteen event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
}

/// <summary>
/// Multi-stream perspective that handles sixteen event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
}

/// <summary>
/// Multi-stream perspective that handles seventeen event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
}

/// <summary>
/// Multi-stream perspective that handles eighteen event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
}

/// <summary>
/// Multi-stream perspective that handles nineteen event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
}

/// <summary>
/// Multi-stream perspective that handles twenty event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
}

/// <summary>
/// Multi-stream perspective that handles twenty-one event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
}

/// <summary>
/// Multi-stream perspective that handles twenty-two event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
}

/// <summary>
/// Multi-stream perspective that handles twenty-three event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
}

/// <summary>
/// Multi-stream perspective that handles twenty-four event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
}

/// <summary>
/// Multi-stream perspective that handles twenty-five event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
}

/// <summary>
/// Multi-stream perspective that handles twenty-six event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
}

/// <summary>
/// Multi-stream perspective that handles twenty-seven event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
}

/// <summary>
/// Multi-stream perspective that handles twenty-eight event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
}

/// <summary>
/// Multi-stream perspective that handles twenty-nine event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
}

/// <summary>
/// Multi-stream perspective that handles thirty event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
}

/// <summary>
/// Multi-stream perspective that handles thirty-one event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
}

/// <summary>
/// Multi-stream perspective that handles thirty-two event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
}

/// <summary>
/// Multi-stream perspective that handles thirty-three event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
}

/// <summary>
/// Multi-stream perspective that handles thirty-four event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
}

/// <summary>
/// Multi-stream perspective that handles thirty-five event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
}

/// <summary>
/// Multi-stream perspective that handles thirty-six event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
}

/// <summary>
/// Multi-stream perspective that handles thirty-seven event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
}

/// <summary>
/// Multi-stream perspective that handles thirty-eight event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
}

/// <summary>
/// Multi-stream perspective that handles thirty-nine event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent39 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent39 eventData);
}

/// <summary>
/// Multi-stream perspective that handles forty event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent39 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent40 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent40 eventData);
}

/// <summary>
/// Multi-stream perspective that handles forty-one event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent39 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent40 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent41 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent41 eventData);
}

/// <summary>
/// Multi-stream perspective that handles forty-two event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent39 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent40 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent41 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent42 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent42 eventData);
}

/// <summary>
/// Multi-stream perspective that handles forty-three event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent39 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent40 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent41 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent42 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent43 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent43 eventData);
}

/// <summary>
/// Multi-stream perspective that handles forty-four event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent39 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent40 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent41 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent42 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent43 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent44 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent44 eventData);
}

/// <summary>
/// Multi-stream perspective that handles forty-five event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent
  where TEvent45 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent39 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent40 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent41 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent42 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent43 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent44 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent45 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent44 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent45 eventData);
}

/// <summary>
/// Multi-stream perspective that handles forty-six event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent
  where TEvent45 : IEvent
  where TEvent46 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent39 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent40 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent41 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent42 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent43 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent44 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent45 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent46 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent44 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent45 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent46 eventData);
}

/// <summary>
/// Multi-stream perspective that handles forty-seven event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent
  where TEvent45 : IEvent
  where TEvent46 : IEvent
  where TEvent47 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent39 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent40 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent41 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent42 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent43 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent44 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent45 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent46 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent47 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent44 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent45 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent46 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent47 eventData);
}

/// <summary>
/// Multi-stream perspective that handles forty-eight event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent
  where TEvent45 : IEvent
  where TEvent46 : IEvent
  where TEvent47 : IEvent
  where TEvent48 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent39 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent40 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent41 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent42 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent43 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent44 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent45 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent46 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent47 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent48 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent44 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent45 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent46 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent47 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent48 eventData);
}

/// <summary>
/// Multi-stream perspective that handles forty-nine event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48, TEvent49> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent
  where TEvent45 : IEvent
  where TEvent46 : IEvent
  where TEvent47 : IEvent
  where TEvent48 : IEvent
  where TEvent49 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent39 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent40 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent41 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent42 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent43 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent44 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent45 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent46 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent47 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent48 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent49 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent44 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent45 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent46 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent47 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent48 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent49 eventData);
}

/// <summary>
/// Multi-stream perspective that handles fifty event types with partition key extraction.
/// </summary>
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48, TEvent49, TEvent50> : IGlobalPerspectiveFor<TModel, TPartitionKey>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent
  where TEvent45 : IEvent
  where TEvent46 : IEvent
  where TEvent47 : IEvent
  where TEvent48 : IEvent
  where TEvent49 : IEvent
  where TEvent50 : IEvent {
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent1 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent2 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent3 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent4 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent5 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent6 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent7 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent8 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent9 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent10 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent11 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent12 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent13 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent14 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent15 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent16 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent17 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent18 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent19 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent20 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent21 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent22 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent23 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent24 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent25 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent26 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent27 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent28 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent29 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent30 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent31 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent32 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent33 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent34 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent35 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent36 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent37 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent38 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent39 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent40 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent41 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent42 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent43 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent44 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent45 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent46 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent47 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent48 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent49 eventData);
  /// <inheritdoc/>
  TPartitionKey GetPartitionKey(TEvent50 eventData);

  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent44 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent45 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent46 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent47 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent48 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent49 eventData);
  /// <inheritdoc/>
  TModel Apply(TModel currentData, TEvent50 eventData);
}
