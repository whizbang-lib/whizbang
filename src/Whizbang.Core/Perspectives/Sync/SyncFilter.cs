namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Static entry points for creating perspective sync filters.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Usage Examples:</strong>
/// </para>
/// <code>
/// // Wait for specific stream
/// var options = SyncFilter.ForStream(orderId).Local().Build();
///
/// // Wait for specific event types
/// var options = SyncFilter.ForEventTypes&lt;OrderCreatedEvent&gt;().Build();
///
/// // Wait for events in current scope
/// var options = SyncFilter.CurrentScope().Local().Build();
///
/// // Complex filter with AND/OR
/// var options = SyncFilter.ForStream(orderId)
///     .AndEventTypes&lt;OrderCreatedEvent, OrderUpdatedEvent&gt;()
///     .Or(SyncFilter.ForEventTypes&lt;OrderCancelledEvent&gt;())
///     .Distributed()
///     .Build();
/// </code>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
public static class SyncFilter {
  /// <summary>
  /// Creates a filter for a specific stream.
  /// </summary>
  /// <param name="streamId">The stream ID to filter by.</param>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder ForStream(Guid streamId) {
    return new SyncFilterBuilder(new StreamFilter(streamId));
  }

  /// <summary>
  /// Creates a filter for a specific event type.
  /// </summary>
  /// <typeparam name="T">The event type to filter by.</typeparam>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder ForEventTypes<T>() {
    return new SyncFilterBuilder(new EventTypeFilter([typeof(T)]));
  }

  /// <summary>
  /// Creates a filter for specific event types.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder ForEventTypes<T1, T2>() {
    return new SyncFilterBuilder(new EventTypeFilter([typeof(T1), typeof(T2)]));
  }

  /// <summary>
  /// Creates a filter for specific event types.
  /// </summary>
  /// <param name="eventTypes">The event types to filter by.</param>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder ForEventTypes(params Type[] eventTypes) {
    ArgumentNullException.ThrowIfNull(eventTypes);
    return new SyncFilterBuilder(new EventTypeFilter(eventTypes));
  }

  /// <summary>
  /// Creates a filter for events emitted in the current scope/request.
  /// </summary>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder CurrentScope() {
    return new SyncFilterBuilder(new CurrentScopeFilter());
  }

  /// <summary>
  /// Creates a filter that matches all pending events.
  /// </summary>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder All() {
    return new SyncFilterBuilder(new AllPendingFilter());
  }
}
