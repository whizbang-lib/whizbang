namespace Whizbang.Core.Messaging;

/// <summary>
/// Read-only LINQ abstraction for querying raw events in the event store.
/// Provides IQueryable access to EventStoreRecord with full LINQ support.
/// Implementation translates LINQ to database-specific queries.
///
/// <para>
/// <strong>Scope Filtering:</strong> When used via <see cref="Lenses.IScopedLensFactory"/>,
/// scope filters (tenant, user, principal) are automatically applied based on the current context.
/// Use <see cref="Lenses.ScopeFilter.None"/> for global/admin access.
/// </para>
///
/// <para>
/// <strong>For singleton services:</strong> Use <see cref="IScopedEventStoreQuery"/> (auto-scoping)
/// or <see cref="IEventStoreQueryFactory"/> (manual scope control).
/// </para>
/// </summary>
/// <docs>fundamentals/events/event-store-query</docs>
/// <tests>Whizbang.Core.Tests/Messaging/IEventStoreQueryTests.cs</tests>
public interface IEventStoreQuery {
  /// <summary>
  /// Queryable access to raw event store records.
  /// Supports filtering, projection, and ordering via LINQ.
  /// Scope filters are automatically applied based on factory context.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Messaging/IEventStoreQueryTests.cs:IEventStoreQuery_HasQueryPropertyAsync</tests>
  IQueryable<EventStoreRecord> Query { get; }

  /// <summary>
  /// Get all events for a specific stream, ordered by version.
  /// Useful for replaying aggregate state or debugging.
  /// </summary>
  /// <param name="streamId">The stream identifier (aggregate ID).</param>
  /// <returns>Events ordered by version ascending.</returns>
  /// <tests>Whizbang.Core.Tests/Messaging/IEventStoreQueryTests.cs:IEventStoreQuery_HasGetStreamEventsMethodAsync</tests>
  IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId);

  /// <summary>
  /// Get all events of a specific type.
  /// Useful for event-driven analytics or cross-aggregate queries.
  /// </summary>
  /// <param name="eventType">The fully-qualified event type name (e.g., "MyApp.Events.OrderPlaced").</param>
  /// <returns>Events matching the specified type.</returns>
  /// <tests>Whizbang.Core.Tests/Messaging/IEventStoreQueryTests.cs:IEventStoreQuery_HasGetEventsByTypeMethodAsync</tests>
  IQueryable<EventStoreRecord> GetEventsByType(string eventType);
}
