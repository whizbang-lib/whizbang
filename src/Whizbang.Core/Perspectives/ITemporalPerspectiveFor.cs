namespace Whizbang.Core.Perspectives;

#pragma warning disable S2326 // Unused type parameters should be removed
#pragma warning disable S2436 // Reduce the number of type parameters in the generic type
// TModel and TEvent1-TEvent10 are intentionally declared at the interface level to document
// which model and event types are valid. The runtime implementation enforces type constraints.
// Supporting up to 10 event types allows complex temporal event handling while maintaining type safety.

/// <summary>
/// Base marker interface for temporal (append-only) perspectives.
/// Unlike <see cref="IPerspectiveFor{TModel}"/> which updates a single row per stream (UPSERT),
/// temporal perspectives INSERT a new row for each event, creating an append-only log.
/// </summary>
/// <typeparam name="TModel">The log entry model type that this perspective produces</typeparam>
/// <docs>perspectives/temporal</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/ITemporalPerspectiveForTests.cs</tests>
/// <remarks>
/// <para>
/// Temporal perspectives are ideal for:
/// <list type="bullet">
///   <item><description>Activity feeds (recent activity for a user)</description></item>
///   <item><description>Audit logs (complete history of changes)</description></item>
///   <item><description>Event sourcing read models that need full history</description></item>
///   <item><description>Time-series data (metrics, analytics)</description></item>
/// </list>
/// </para>
/// <para>
/// Key differences from <see cref="IPerspectiveFor{TModel}"/>:
/// <list type="bullet">
///   <item><description>Uses <c>Transform(event)</c> instead of <c>Apply(currentData, event)</c></description></item>
///   <item><description>No current state needed - each event is independently transformed</description></item>
///   <item><description>Returns <c>TModel?</c> - null skips the event (no entry created)</description></item>
///   <item><description>Always INSERT, never UPDATE - creates append-only history</description></item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class ActivityPerspective :
///     ITemporalPerspectiveFor&lt;ActivityEntry, OrderCreatedEvent, OrderUpdatedEvent&gt; {
///
///   public ActivityEntry? Transform(OrderCreatedEvent @event) {
///     return new ActivityEntry {
///       SubjectId = @event.OrderId,
///       Action = "created",
///       Description = $"Order created for ${@event.Amount}"
///     };
///   }
///
///   public ActivityEntry? Transform(OrderUpdatedEvent @event) {
///     return new ActivityEntry {
///       SubjectId = @event.OrderId,
///       Action = "updated",
///       Description = $"Order status changed to {@event.NewStatus}"
///     };
///   }
/// }
/// </code>
/// </example>
public interface ITemporalPerspectiveFor<TModel> where TModel : class {
  // Marker interface - no methods required
  // Specific event handling enforced by ITemporalPerspectiveFor<TModel, TEvent> variants
}

/// <summary>
/// Temporal perspective that transforms a single event type to log entries.
/// Each event creates a NEW row (INSERT), never updates existing rows (no UPSERT).
/// </summary>
/// <typeparam name="TModel">The log entry model type</typeparam>
/// <typeparam name="TEvent1">The event type this perspective handles</typeparam>
/// <docs>perspectives/temporal</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/ITemporalPerspectiveForTests.cs</tests>
public interface ITemporalPerspectiveFor<TModel, TEvent1> : ITemporalPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent {
  /// <summary>
  /// Transforms an event to a log entry. Return null to skip the event (no entry created).
  /// MUST be a pure function: no I/O, no side effects, deterministic.
  /// </summary>
  /// <param name="eventData">The event that occurred</param>
  /// <returns>A new log entry, or null to skip this event</returns>
  TModel? Transform(TEvent1 eventData);
}

/// <summary>
/// Temporal perspective that transforms two event types to log entries.
/// </summary>
public interface ITemporalPerspectiveFor<TModel, TEvent1, TEvent2> : ITemporalPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent {
  TModel? Transform(TEvent1 eventData);
  TModel? Transform(TEvent2 eventData);
}

/// <summary>
/// Temporal perspective that transforms three event types to log entries.
/// </summary>
public interface ITemporalPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3> : ITemporalPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent {
  TModel? Transform(TEvent1 eventData);
  TModel? Transform(TEvent2 eventData);
  TModel? Transform(TEvent3 eventData);
}

/// <summary>
/// Temporal perspective that transforms four event types to log entries.
/// </summary>
public interface ITemporalPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4> : ITemporalPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent {
  TModel? Transform(TEvent1 eventData);
  TModel? Transform(TEvent2 eventData);
  TModel? Transform(TEvent3 eventData);
  TModel? Transform(TEvent4 eventData);
}

/// <summary>
/// Temporal perspective that transforms five event types to log entries.
/// </summary>
public interface ITemporalPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5> : ITemporalPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent {
  TModel? Transform(TEvent1 eventData);
  TModel? Transform(TEvent2 eventData);
  TModel? Transform(TEvent3 eventData);
  TModel? Transform(TEvent4 eventData);
  TModel? Transform(TEvent5 eventData);
}

/// <summary>
/// Temporal perspective that transforms six event types to log entries.
/// </summary>
public interface ITemporalPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6> : ITemporalPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent {
  TModel? Transform(TEvent1 eventData);
  TModel? Transform(TEvent2 eventData);
  TModel? Transform(TEvent3 eventData);
  TModel? Transform(TEvent4 eventData);
  TModel? Transform(TEvent5 eventData);
  TModel? Transform(TEvent6 eventData);
}

/// <summary>
/// Temporal perspective that transforms seven event types to log entries.
/// </summary>
public interface ITemporalPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7> : ITemporalPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent {
  TModel? Transform(TEvent1 eventData);
  TModel? Transform(TEvent2 eventData);
  TModel? Transform(TEvent3 eventData);
  TModel? Transform(TEvent4 eventData);
  TModel? Transform(TEvent5 eventData);
  TModel? Transform(TEvent6 eventData);
  TModel? Transform(TEvent7 eventData);
}

/// <summary>
/// Temporal perspective that transforms eight event types to log entries.
/// </summary>
public interface ITemporalPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8> : ITemporalPerspectiveFor<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent {
  TModel? Transform(TEvent1 eventData);
  TModel? Transform(TEvent2 eventData);
  TModel? Transform(TEvent3 eventData);
  TModel? Transform(TEvent4 eventData);
  TModel? Transform(TEvent5 eventData);
  TModel? Transform(TEvent6 eventData);
  TModel? Transform(TEvent7 eventData);
  TModel? Transform(TEvent8 eventData);
}

/// <summary>
/// Temporal perspective that transforms nine event types to log entries.
/// </summary>
public interface ITemporalPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9> : ITemporalPerspectiveFor<TModel>
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
  TModel? Transform(TEvent1 eventData);
  TModel? Transform(TEvent2 eventData);
  TModel? Transform(TEvent3 eventData);
  TModel? Transform(TEvent4 eventData);
  TModel? Transform(TEvent5 eventData);
  TModel? Transform(TEvent6 eventData);
  TModel? Transform(TEvent7 eventData);
  TModel? Transform(TEvent8 eventData);
  TModel? Transform(TEvent9 eventData);
}

/// <summary>
/// Temporal perspective that transforms ten event types to log entries.
/// </summary>
public interface ITemporalPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10> : ITemporalPerspectiveFor<TModel>
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
  TModel? Transform(TEvent1 eventData);
  TModel? Transform(TEvent2 eventData);
  TModel? Transform(TEvent3 eventData);
  TModel? Transform(TEvent4 eventData);
  TModel? Transform(TEvent5 eventData);
  TModel? Transform(TEvent6 eventData);
  TModel? Transform(TEvent7 eventData);
  TModel? Transform(TEvent8 eventData);
  TModel? Transform(TEvent9 eventData);
  TModel? Transform(TEvent10 eventData);
}
