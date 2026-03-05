namespace Whizbang.Core.Perspectives;

#pragma warning disable S2436 // Reduce the number of type parameters in the generic type
// TModel and TEvent1-TEvent10 are intentionally declared at the interface level to document
// which model and event types are valid. The runtime implementation enforces type constraints.
// Supporting up to 10 event types allows complex event handling scenarios while maintaining type safety.

/// <summary>
/// Base marker interface for perspectives that return ApplyResult with action support.
/// Use this interface when your perspective needs to express deletion operations
/// (soft delete or hard delete/purge) in addition to normal model updates.
/// </summary>
/// <typeparam name="TModel">The read model type that this perspective maintains</typeparam>
/// <remarks>
/// <para>
/// <strong>When to use:</strong> Use <see cref="IPerspectiveWithActionsFor{TModel}"/> instead of
/// <see cref="IPerspectiveFor{TModel}"/> when you need to return deletion actions from Apply methods.
/// </para>
/// <para>
/// <strong>Mixing interfaces:</strong> A perspective class can implement both
/// <see cref="IPerspectiveFor{TModel,TEvent}"/> and <see cref="IPerspectiveWithActionsFor{TModel,TEvent}"/>
/// for different event types. Use <see cref="IPerspectiveFor{TModel,TEvent}"/> for events that only
/// update the model, and <see cref="IPerspectiveWithActionsFor{TModel,TEvent}"/> for events that
/// may delete the model.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// class OrderPerspective :
///     IPerspectiveFor&lt;OrderView, OrderCreated&gt;,              // Updates only
///     IPerspectiveFor&lt;OrderView, OrderUpdated&gt;,              // Updates only
///     IPerspectiveWithActionsFor&lt;OrderView, OrderCancelled&gt;, // May delete
///     IPerspectiveWithActionsFor&lt;OrderView, OrderArchived&gt;   // May delete
/// {
///     public OrderView Apply(OrderView c, OrderCreated e) =&gt; new(...);
///     public OrderView Apply(OrderView c, OrderUpdated e) =&gt; c with { ... };
///     public ApplyResult&lt;OrderView&gt; Apply(OrderView c, OrderCancelled e)
///         =&gt; ApplyResult&lt;OrderView&gt;.Delete();
///     public ApplyResult&lt;OrderView&gt; Apply(OrderView c, OrderArchived e)
///         =&gt; ApplyResult&lt;OrderView&gt;.Purge();
/// }
/// </code>
/// </example>
/// <docs>core-concepts/perspectives-with-actions</docs>
public interface IPerspectiveWithActionsFor<TModel> where TModel : class {
  // Marker interface - no methods required
  // Specific event handling enforced by IPerspectiveWithActionsFor<TModel, TEvent> variants
}

/// <summary>
/// Perspective with action support for a single event type.
/// Returns <see cref="ApplyResult{TModel}"/> to support Delete/Purge operations.
/// </summary>
/// <typeparam name="TModel">The read model type</typeparam>
/// <typeparam name="TEvent1">The event type this perspective handles</typeparam>
/// <remarks>
/// <para>
/// <strong>Return Type:</strong> Apply returns <see cref="ApplyResult{TModel}"/> which supports:
/// </para>
/// <list type="bullet">
/// <item>Returning an updated model (implicit conversion from TModel)</item>
/// <item>Soft delete via <c>ApplyResult&lt;TModel&gt;.Delete()</c></item>
/// <item>Hard delete via <c>ApplyResult&lt;TModel&gt;.Purge()</c></item>
/// <item>No change via <c>ApplyResult&lt;TModel&gt;.None()</c></item>
/// </list>
/// </remarks>
/// <docs>core-concepts/perspectives-with-actions</docs>
public interface IPerspectiveWithActionsFor<TModel, TEvent1> : IPerspectiveWithActionsFor<TModel>
  where TModel : class
  where TEvent1 : IEvent {
  /// <summary>
  /// Applies an event to the model and returns an apply result.
  /// MUST be a pure function: no I/O, no side effects, deterministic.
  /// </summary>
  /// <param name="currentData">The current state of the read model</param>
  /// <param name="eventData">The event that occurred</param>
  /// <returns>
  /// An <see cref="ApplyResult{TModel}"/> containing the updated model and/or action.
  /// Can return a TModel directly (implicit conversion) or use factory methods like
  /// <c>ApplyResult&lt;TModel&gt;.Delete()</c> for soft deletes.
  /// </returns>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
}

/// <summary>
/// Perspective with action support for two event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2> : IPerspectiveWithActionsFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent {
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
}

/// <summary>
/// Perspective with action support for three event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3> : IPerspectiveWithActionsFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent {
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
}

/// <summary>
/// Perspective with action support for four event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4> : IPerspectiveWithActionsFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent {
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
}

/// <summary>
/// Perspective with action support for five event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5> : IPerspectiveWithActionsFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent {
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
}

/// <summary>
/// Perspective with action support for six event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6> : IPerspectiveWithActionsFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent {
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
}

/// <summary>
/// Perspective with action support for seven event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7> : IPerspectiveWithActionsFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent {
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
}

/// <summary>
/// Perspective with action support for eight event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8> : IPerspectiveWithActionsFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent {
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
}

/// <summary>
/// Perspective with action support for nine event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9> : IPerspectiveWithActionsFor<TModel>
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
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
}

/// <summary>
/// Perspective with action support for ten event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10> : IPerspectiveWithActionsFor<TModel>
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
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
}
