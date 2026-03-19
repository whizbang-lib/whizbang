namespace Whizbang.Core.Perspectives;

#pragma warning disable S2326 // Unused type parameters should be removed
#pragma warning disable S2436 // Reduce the number of type parameters in the generic type
// TModel and TEvent1-TEvent20 are intentionally declared at the interface level to document
// which model and event types are valid. The runtime implementation enforces type constraints.
// Supporting up to 20 event types allows complex event handling scenarios while maintaining type safety.

/// <summary>
/// Base marker interface for single-stream perspectives.
/// Perspectives listen to events and update read models (projections/views).
/// They are eventually-consistent denormalized views optimized for queries.
/// </summary>
/// <typeparam name="TModel">The read model type that this perspective maintains</typeparam>
/// <docs>fundamentals/perspectives/perspectives</docs>
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
/// <docs>fundamentals/perspectives/perspectives</docs>
public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent {
  /// <summary>
  /// Applies an event to the model and returns a new model.
  /// MUST be a pure function: no I/O, no side effects, deterministic.
  /// Use event timestamps instead of DateTime.UtcNow for time values.
  /// </summary>
  /// <param name="currentData">The current state of the read model</param>
  /// <param name="eventData">The event that occurred</param>
  /// <returns>A new model with the event applied</returns>
  TModel Apply(TModel currentData, TEvent1 eventData);
}

/// <summary>
/// Perspective that handles two event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
}

/// <summary>
/// Perspective that handles three event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
}

/// <summary>
/// Perspective that handles six event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
}

/// <summary>
/// Perspective that handles seven event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
}

/// <summary>
/// Perspective that handles eight event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
}

/// <summary>
/// Perspective that handles nine event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData);
}

/// <summary>
/// Perspective that handles ten event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10> : IPerspectiveFor<TModel>
  where TModel : class
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData);
  TModel Apply(TModel currentData, TEvent10 eventData);
}

/// <summary>
/// Perspective that handles eleven event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11> : IPerspectiveFor<TModel>
  where TModel : class
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData);
  TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData);
}

/// <summary>
/// Perspective that handles twelve event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12> : IPerspectiveFor<TModel>
  where TModel : class
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData);
  TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData);
  TModel Apply(TModel currentData, TEvent12 eventData);
}

/// <summary>
/// Perspective that handles thirteen event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13> : IPerspectiveFor<TModel>
  where TModel : class
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData);
  TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData);
  TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData);
}

/// <summary>
/// Perspective that handles fourteen event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14> : IPerspectiveFor<TModel>
  where TModel : class
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData);
  TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData);
  TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData);
  TModel Apply(TModel currentData, TEvent14 eventData);
}

/// <summary>
/// Perspective that handles fifteen event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15> : IPerspectiveFor<TModel>
  where TModel : class
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData);
  TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData);
  TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData);
  TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData);
}

/// <summary>
/// Perspective that handles sixteen event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16> : IPerspectiveFor<TModel>
  where TModel : class
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData);
  TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData);
  TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData);
  TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData);
  TModel Apply(TModel currentData, TEvent16 eventData);
}

/// <summary>
/// Perspective that handles seventeen event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17> : IPerspectiveFor<TModel>
  where TModel : class
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData);
  TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData);
  TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData);
  TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData);
  TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData);
}

/// <summary>
/// Perspective that handles eighteen event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18> : IPerspectiveFor<TModel>
  where TModel : class
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData);
  TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData);
  TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData);
  TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData);
  TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData);
  TModel Apply(TModel currentData, TEvent18 eventData);
}

/// <summary>
/// Perspective that handles nineteen event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19> : IPerspectiveFor<TModel>
  where TModel : class
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData);
  TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData);
  TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData);
  TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData);
  TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData);
  TModel Apply(TModel currentData, TEvent18 eventData);
  TModel Apply(TModel currentData, TEvent19 eventData);
}

/// <summary>
/// Perspective that handles twenty event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20> : IPerspectiveFor<TModel>
  where TModel : class
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
  TModel Apply(TModel currentData, TEvent1 eventData);
  TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData);
  TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData);
  TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData);
  TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData);
  TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData);
  TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData);
  TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData);
  TModel Apply(TModel currentData, TEvent18 eventData);
  TModel Apply(TModel currentData, TEvent19 eventData);
  TModel Apply(TModel currentData, TEvent20 eventData);
}

/// <summary>
/// Perspective that handles twenty-one event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData);
  TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData);
}

/// <summary>
/// Perspective that handles twenty-two event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData);
  TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData);
}

/// <summary>
/// Perspective that handles twenty-three event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData);
  TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData);
  TModel Apply(TModel currentData, TEvent23 eventData);
}

/// <summary>
/// Perspective that handles twenty-four event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData);
  TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData);
  TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData);
}

/// <summary>
/// Perspective that handles twenty-five event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData);
  TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData);
  TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData);
  TModel Apply(TModel currentData, TEvent25 eventData);
}

/// <summary>
/// Perspective that handles twenty-six event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent
  where TEvent26 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData);
  TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData);
  TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData);
  TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData);
}

/// <summary>
/// Perspective that handles twenty-seven event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent
  where TEvent26 : IEvent where TEvent27 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData);
  TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData);
  TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData);
  TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData);
  TModel Apply(TModel currentData, TEvent27 eventData);
}

/// <summary>
/// Perspective that handles twenty-eight event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent
  where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData);
  TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData);
  TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData);
  TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData);
  TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData);
}

/// <summary>
/// Perspective that handles twenty-nine event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent
  where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData);
  TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData);
  TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData);
  TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData);
  TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData);
  TModel Apply(TModel currentData, TEvent29 eventData);
}

/// <summary>
/// Perspective that handles thirty event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent
  where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData);
  TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData);
  TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData);
  TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData);
  TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData);
  TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData);
  TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData);
  TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData);
  TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData);
  TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData);
  TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData);
  TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData);
  TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData);
}

/// <summary>
/// Perspective that handles thirty-one event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent
  where TEvent31 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData);
  TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData);
  TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData);
  TModel Apply(TModel currentData, TEvent31 eventData);
}

/// <summary>
/// Perspective that handles thirty-two event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent
  where TEvent31 : IEvent where TEvent32 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData);
  TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData);
  TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData);
  TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData);
}

/// <summary>
/// Perspective that handles thirty-three event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent
  where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData);
  TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData);
  TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData);
  TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData);
}

/// <summary>
/// Perspective that handles thirty-four event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent
  where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData);
  TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData);
  TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData);
  TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData);
}

/// <summary>
/// Perspective that handles thirty-five event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35> : IPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent
  where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData);
  TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData);
  TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData);
  TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData);
  TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData);
  TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData);
  TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData);
}

/// <summary>
/// Perspective that handles thirty-six event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData);
}

/// <summary>
/// Perspective that handles thirty-seven event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData);
}

/// <summary>
/// Perspective that handles thirty-eight event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData);
}

/// <summary>
/// Perspective that handles thirty-nine event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData); TModel Apply(TModel currentData, TEvent39 eventData);
}

/// <summary>
/// Perspective that handles forty event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData); TModel Apply(TModel currentData, TEvent39 eventData); TModel Apply(TModel currentData, TEvent40 eventData);
}

/// <summary>
/// Perspective that handles forty-one event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData); TModel Apply(TModel currentData, TEvent39 eventData); TModel Apply(TModel currentData, TEvent40 eventData); TModel Apply(TModel currentData, TEvent41 eventData);
}

/// <summary>
/// Perspective that handles forty-two event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData); TModel Apply(TModel currentData, TEvent39 eventData); TModel Apply(TModel currentData, TEvent40 eventData); TModel Apply(TModel currentData, TEvent41 eventData); TModel Apply(TModel currentData, TEvent42 eventData);
}

/// <summary>
/// Perspective that handles forty-three event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData); TModel Apply(TModel currentData, TEvent39 eventData); TModel Apply(TModel currentData, TEvent40 eventData); TModel Apply(TModel currentData, TEvent41 eventData); TModel Apply(TModel currentData, TEvent42 eventData); TModel Apply(TModel currentData, TEvent43 eventData);
}

/// <summary>
/// Perspective that handles forty-four event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData); TModel Apply(TModel currentData, TEvent39 eventData); TModel Apply(TModel currentData, TEvent40 eventData); TModel Apply(TModel currentData, TEvent41 eventData); TModel Apply(TModel currentData, TEvent42 eventData); TModel Apply(TModel currentData, TEvent43 eventData); TModel Apply(TModel currentData, TEvent44 eventData);
}

/// <summary>
/// Perspective that handles forty-five event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent where TEvent45 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData); TModel Apply(TModel currentData, TEvent39 eventData); TModel Apply(TModel currentData, TEvent40 eventData); TModel Apply(TModel currentData, TEvent41 eventData); TModel Apply(TModel currentData, TEvent42 eventData); TModel Apply(TModel currentData, TEvent43 eventData); TModel Apply(TModel currentData, TEvent44 eventData); TModel Apply(TModel currentData, TEvent45 eventData);
}

/// <summary>
/// Perspective that handles forty-six event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent where TEvent45 : IEvent where TEvent46 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData); TModel Apply(TModel currentData, TEvent39 eventData); TModel Apply(TModel currentData, TEvent40 eventData); TModel Apply(TModel currentData, TEvent41 eventData); TModel Apply(TModel currentData, TEvent42 eventData); TModel Apply(TModel currentData, TEvent43 eventData); TModel Apply(TModel currentData, TEvent44 eventData); TModel Apply(TModel currentData, TEvent45 eventData); TModel Apply(TModel currentData, TEvent46 eventData);
}

/// <summary>
/// Perspective that handles forty-seven event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent where TEvent45 : IEvent where TEvent46 : IEvent where TEvent47 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData); TModel Apply(TModel currentData, TEvent39 eventData); TModel Apply(TModel currentData, TEvent40 eventData); TModel Apply(TModel currentData, TEvent41 eventData); TModel Apply(TModel currentData, TEvent42 eventData); TModel Apply(TModel currentData, TEvent43 eventData); TModel Apply(TModel currentData, TEvent44 eventData); TModel Apply(TModel currentData, TEvent45 eventData); TModel Apply(TModel currentData, TEvent46 eventData); TModel Apply(TModel currentData, TEvent47 eventData);
}

/// <summary>
/// Perspective that handles forty-eight event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent where TEvent45 : IEvent where TEvent46 : IEvent where TEvent47 : IEvent where TEvent48 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData); TModel Apply(TModel currentData, TEvent39 eventData); TModel Apply(TModel currentData, TEvent40 eventData); TModel Apply(TModel currentData, TEvent41 eventData); TModel Apply(TModel currentData, TEvent42 eventData); TModel Apply(TModel currentData, TEvent43 eventData); TModel Apply(TModel currentData, TEvent44 eventData); TModel Apply(TModel currentData, TEvent45 eventData); TModel Apply(TModel currentData, TEvent46 eventData); TModel Apply(TModel currentData, TEvent47 eventData); TModel Apply(TModel currentData, TEvent48 eventData);
}

/// <summary>
/// Perspective that handles forty-nine event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48, TEvent49> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent where TEvent45 : IEvent where TEvent46 : IEvent where TEvent47 : IEvent where TEvent48 : IEvent where TEvent49 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData); TModel Apply(TModel currentData, TEvent39 eventData); TModel Apply(TModel currentData, TEvent40 eventData); TModel Apply(TModel currentData, TEvent41 eventData); TModel Apply(TModel currentData, TEvent42 eventData); TModel Apply(TModel currentData, TEvent43 eventData); TModel Apply(TModel currentData, TEvent44 eventData); TModel Apply(TModel currentData, TEvent45 eventData); TModel Apply(TModel currentData, TEvent46 eventData); TModel Apply(TModel currentData, TEvent47 eventData); TModel Apply(TModel currentData, TEvent48 eventData); TModel Apply(TModel currentData, TEvent49 eventData);
}

/// <summary>
/// Perspective that handles fifty event types with pure function Apply methods.
/// </summary>
public interface IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48, TEvent49, TEvent50> : IPerspectiveFor<TModel>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent where TEvent45 : IEvent where TEvent46 : IEvent where TEvent47 : IEvent where TEvent48 : IEvent where TEvent49 : IEvent where TEvent50 : IEvent {
  TModel Apply(TModel currentData, TEvent1 eventData); TModel Apply(TModel currentData, TEvent2 eventData); TModel Apply(TModel currentData, TEvent3 eventData); TModel Apply(TModel currentData, TEvent4 eventData); TModel Apply(TModel currentData, TEvent5 eventData); TModel Apply(TModel currentData, TEvent6 eventData); TModel Apply(TModel currentData, TEvent7 eventData); TModel Apply(TModel currentData, TEvent8 eventData); TModel Apply(TModel currentData, TEvent9 eventData); TModel Apply(TModel currentData, TEvent10 eventData); TModel Apply(TModel currentData, TEvent11 eventData); TModel Apply(TModel currentData, TEvent12 eventData); TModel Apply(TModel currentData, TEvent13 eventData); TModel Apply(TModel currentData, TEvent14 eventData); TModel Apply(TModel currentData, TEvent15 eventData); TModel Apply(TModel currentData, TEvent16 eventData); TModel Apply(TModel currentData, TEvent17 eventData); TModel Apply(TModel currentData, TEvent18 eventData); TModel Apply(TModel currentData, TEvent19 eventData); TModel Apply(TModel currentData, TEvent20 eventData); TModel Apply(TModel currentData, TEvent21 eventData); TModel Apply(TModel currentData, TEvent22 eventData); TModel Apply(TModel currentData, TEvent23 eventData); TModel Apply(TModel currentData, TEvent24 eventData); TModel Apply(TModel currentData, TEvent25 eventData); TModel Apply(TModel currentData, TEvent26 eventData); TModel Apply(TModel currentData, TEvent27 eventData); TModel Apply(TModel currentData, TEvent28 eventData); TModel Apply(TModel currentData, TEvent29 eventData); TModel Apply(TModel currentData, TEvent30 eventData); TModel Apply(TModel currentData, TEvent31 eventData); TModel Apply(TModel currentData, TEvent32 eventData); TModel Apply(TModel currentData, TEvent33 eventData); TModel Apply(TModel currentData, TEvent34 eventData); TModel Apply(TModel currentData, TEvent35 eventData); TModel Apply(TModel currentData, TEvent36 eventData); TModel Apply(TModel currentData, TEvent37 eventData); TModel Apply(TModel currentData, TEvent38 eventData); TModel Apply(TModel currentData, TEvent39 eventData); TModel Apply(TModel currentData, TEvent40 eventData); TModel Apply(TModel currentData, TEvent41 eventData); TModel Apply(TModel currentData, TEvent42 eventData); TModel Apply(TModel currentData, TEvent43 eventData); TModel Apply(TModel currentData, TEvent44 eventData); TModel Apply(TModel currentData, TEvent45 eventData); TModel Apply(TModel currentData, TEvent46 eventData); TModel Apply(TModel currentData, TEvent47 eventData); TModel Apply(TModel currentData, TEvent48 eventData); TModel Apply(TModel currentData, TEvent49 eventData); TModel Apply(TModel currentData, TEvent50 eventData);
}
