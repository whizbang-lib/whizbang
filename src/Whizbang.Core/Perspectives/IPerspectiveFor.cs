namespace Whizbang.Core.Perspectives;

/// <summary>
/// Base marker interface for single-stream perspectives.
/// Perspectives listen to events and update read models (projections/views).
/// They are eventually-consistent denormalized views optimized for queries.
/// </summary>
/// <typeparam name="TModel">The read model type that this perspective maintains</typeparam>
/// <docs>core-concepts/perspectives</docs>
public interface IPerspectiveFor<TModel> where TModel : class {
  // Marker interface - no methods required
  // Specific event handling enforced by IPerspectiveFor<TModel, TEvent> variants
}

/// <summary>
/// Perspective that handles a single event type with pure function Apply method.
/// Apply methods must be pure functions: no I/O, no side effects, deterministic.
/// </summary>
/// <typeparam name="TModel">The read model type</typeparam>
/// <typeparam name="TEvent1">The event type this perspective handles</typeparam>
/// <docs>core-concepts/perspectives</docs>
public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent {
  /// <summary>
  /// Applies an event to the model and returns a new model.
  /// MUST be a pure function: no I/O, no side effects, deterministic.
  /// Use event timestamps instead of DateTime.UtcNow for time values.
  /// </summary>
  /// <param name="currentData">The current state of the read model</param>
  /// <param name="event">The event that occurred</param>
  /// <returns>A new model with the event applied</returns>
  TModel Apply(TModel currentData, TEvent1 @event);
}

/// <summary>
/// Perspective that handles two event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent {
  TModel Apply(TModel currentData, TEvent1 @event);
  TModel Apply(TModel currentData, TEvent2 @event);
}

/// <summary>
/// Perspective that handles three event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent {
  TModel Apply(TModel currentData, TEvent1 @event);
  TModel Apply(TModel currentData, TEvent2 @event);
  TModel Apply(TModel currentData, TEvent3 @event);
}

/// <summary>
/// Perspective that handles four event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent {
  TModel Apply(TModel currentData, TEvent1 @event);
  TModel Apply(TModel currentData, TEvent2 @event);
  TModel Apply(TModel currentData, TEvent3 @event);
  TModel Apply(TModel currentData, TEvent4 @event);
}

/// <summary>
/// Perspective that handles five event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent {
  TModel Apply(TModel currentData, TEvent1 @event);
  TModel Apply(TModel currentData, TEvent2 @event);
  TModel Apply(TModel currentData, TEvent3 @event);
  TModel Apply(TModel currentData, TEvent4 @event);
  TModel Apply(TModel currentData, TEvent5 @event);
}

// TODO: Generate remaining variants (6-50 event types) via source generator or T4 template
// For now, we have enough to validate the pattern works
