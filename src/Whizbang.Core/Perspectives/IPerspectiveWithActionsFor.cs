// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

#pragma warning disable S2436 // Reduce the number of type parameters in the generic type
// TModel and TEvent1-TEvent50 are intentionally declared at the interface level to document
// which model and event types are valid. The runtime implementation enforces type constraints.
// Up to 50 event types, the same as IPerspectiveFor, so moving an event between the families never meets a smaller cap.

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
///     IPerspectiveWithActionsFor&lt;OrderView, OrderCanceled&gt;, // May delete
///     IPerspectiveWithActionsFor&lt;OrderView, OrderArchived&gt;   // May delete
/// {
///     public OrderView Apply(OrderView c, OrderCreated e) =&gt; new(...);
///     public OrderView Apply(OrderView c, OrderUpdated e) =&gt; c with { ... };
///     public ApplyResult&lt;OrderView&gt; Apply(OrderView c, OrderCanceled e)
///         =&gt; ApplyResult&lt;OrderView&gt;.Delete();
///     public ApplyResult&lt;OrderView&gt; Apply(OrderView c, OrderArchived e)
///         =&gt; ApplyResult&lt;OrderView&gt;.Purge();
/// }
/// </code>
/// </example>
/// <docs>fundamentals/perspectives/perspectives-with-actions</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveWithActionsArityTests.cs:BothFamilies_TakeUpToFiftyEvents_AndNoMoreAsync</tests>
[global::System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S2326:Unused type parameters should be removed", Justification = "The type parameter is the key the source generator and the container resolve on; the interface is a marker by design.")]
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
/// <docs>fundamentals/perspectives/perspectives-with-actions</docs>
public interface IPerspectiveWithActionsFor<TModel, TEvent1> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1>
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
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
}

/// <summary>
/// Perspective with action support for three event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
}

/// <summary>
/// Perspective with action support for four event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
}

/// <summary>
/// Perspective with action support for five event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
}

/// <summary>
/// Perspective with action support for six event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
}

/// <summary>
/// Perspective with action support for seven event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
}

/// <summary>
/// Perspective with action support for eight event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
}

/// <summary>
/// Perspective with action support for nine event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9>
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
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
}

/// <summary>
/// Perspective with action support for ten event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10>
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
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
}

/// <summary>
/// Perspective with action support for eleven event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11>
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
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
}

/// <summary>
/// Perspective with action support for twelve event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12>
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
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
}

/// <summary>
/// Perspective with action support for thirteen event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13>
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
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
}

/// <summary>
/// Perspective with action support for fourteen event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14>
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
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
}

/// <summary>
/// Perspective with action support for fifteen event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15>
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
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
}

/// <summary>
/// Perspective with action support for sixteen event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16>
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
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
}

/// <summary>
/// Perspective with action support for seventeen event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17>
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
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
}

/// <summary>
/// Perspective with action support for eighteen event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18>
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
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
}

/// <summary>
/// Perspective with action support for nineteen event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19>
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
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
}

/// <summary>
/// Perspective with action support for twenty event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20>
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
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
}

/// <summary>
/// Perspective with action support for twenty-one event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
}

/// <summary>
/// Perspective with action support for twenty-two event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
}

/// <summary>
/// Perspective with action support for twenty-three event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
}

/// <summary>
/// Perspective with action support for twenty-four event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
}

/// <summary>
/// Perspective with action support for twenty-five event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
}

/// <summary>
/// Perspective with action support for twenty-six event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent
  where TEvent26 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
}

/// <summary>
/// Perspective with action support for twenty-seven event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent
  where TEvent26 : IEvent where TEvent27 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
}

/// <summary>
/// Perspective with action support for twenty-eight event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent
  where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
}

/// <summary>
/// Perspective with action support for twenty-nine event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent
  where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
}

/// <summary>
/// Perspective with action support for thirty event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent
  where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent
  where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent
  where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
}

/// <summary>
/// Perspective with action support for thirty-one event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent
  where TEvent31 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
}

/// <summary>
/// Perspective with action support for thirty-two event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent
  where TEvent31 : IEvent where TEvent32 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
}

/// <summary>
/// Perspective with action support for thirty-three event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent
  where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
}

/// <summary>
/// Perspective with action support for thirty-four event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent
  where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
}

/// <summary>
/// Perspective with action support for thirty-five event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35>
  where TModel : class
  where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent
  where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent
  where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent
  where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
}

/// <summary>
/// Perspective with action support for thirty-six event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
}

/// <summary>
/// Perspective with action support for thirty-seven event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
}

/// <summary>
/// Perspective with action support for thirty-eight event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
}

/// <summary>
/// Perspective with action support for thirty-nine event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent39 eventData);
}

/// <summary>
/// Perspective with action support for forty event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent40 eventData);
}

/// <summary>
/// Perspective with action support for forty-one event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent41 eventData);
}

/// <summary>
/// Perspective with action support for forty-two event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent42 eventData);
}

/// <summary>
/// Perspective with action support for forty-three event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent43 eventData);
}

/// <summary>
/// Perspective with action support for forty-four event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent44 eventData);
}

/// <summary>
/// Perspective with action support for forty-five event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent where TEvent45 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent44 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent45 eventData);
}

/// <summary>
/// Perspective with action support for forty-six event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent where TEvent45 : IEvent where TEvent46 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent44 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent45 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent46 eventData);
}

/// <summary>
/// Perspective with action support for forty-seven event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent where TEvent45 : IEvent where TEvent46 : IEvent where TEvent47 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent44 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent45 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent46 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent47 eventData);
}

/// <summary>
/// Perspective with action support for forty-eight event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent where TEvent45 : IEvent where TEvent46 : IEvent where TEvent47 : IEvent where TEvent48 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent44 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent45 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent46 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent47 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent48 eventData);
}

/// <summary>
/// Perspective with action support for forty-nine event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48, TEvent49> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48, TEvent49>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent where TEvent45 : IEvent where TEvent46 : IEvent where TEvent47 : IEvent where TEvent48 : IEvent where TEvent49 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent44 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent45 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent46 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent47 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent48 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent49 eventData);
}

/// <summary>
/// Perspective with action support for fifty event types.
/// </summary>
public interface IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48, TEvent49, TEvent50> : IPerspectiveWithActionsFor<TModel>, IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48, TEvent49, TEvent50>
  where TModel : class where TEvent1 : IEvent where TEvent2 : IEvent where TEvent3 : IEvent where TEvent4 : IEvent where TEvent5 : IEvent where TEvent6 : IEvent where TEvent7 : IEvent where TEvent8 : IEvent where TEvent9 : IEvent where TEvent10 : IEvent where TEvent11 : IEvent where TEvent12 : IEvent where TEvent13 : IEvent where TEvent14 : IEvent where TEvent15 : IEvent where TEvent16 : IEvent where TEvent17 : IEvent where TEvent18 : IEvent where TEvent19 : IEvent where TEvent20 : IEvent where TEvent21 : IEvent where TEvent22 : IEvent where TEvent23 : IEvent where TEvent24 : IEvent where TEvent25 : IEvent where TEvent26 : IEvent where TEvent27 : IEvent where TEvent28 : IEvent where TEvent29 : IEvent where TEvent30 : IEvent where TEvent31 : IEvent where TEvent32 : IEvent where TEvent33 : IEvent where TEvent34 : IEvent where TEvent35 : IEvent where TEvent36 : IEvent where TEvent37 : IEvent where TEvent38 : IEvent where TEvent39 : IEvent where TEvent40 : IEvent where TEvent41 : IEvent where TEvent42 : IEvent where TEvent43 : IEvent where TEvent44 : IEvent where TEvent45 : IEvent where TEvent46 : IEvent where TEvent47 : IEvent where TEvent48 : IEvent where TEvent49 : IEvent where TEvent50 : IEvent {
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent1 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent2 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent3 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent4 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent5 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent6 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent7 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent8 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent9 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent10 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent11 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent12 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent13 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent14 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent15 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent16 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent17 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent18 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent19 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent20 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent21 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent22 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent23 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent24 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent25 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent26 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent27 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent28 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent29 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent30 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent31 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent32 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent33 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent34 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent35 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent36 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent37 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent38 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent39 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent40 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent41 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent42 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent43 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent44 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent45 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent46 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent47 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent48 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent49 eventData);
  /// <inheritdoc/>
  ApplyResult<TModel> Apply(TModel currentData, TEvent50 eventData);
}
