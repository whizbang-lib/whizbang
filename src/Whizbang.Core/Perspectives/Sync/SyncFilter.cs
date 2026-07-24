#pragma warning disable S2436 // Fluent API with intentional generic type parameter overloads

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
/// <docs>fundamentals/perspectives/perspective-sync</docs>
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
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder ForEventTypes<T1, T2, T3>() {
    return new SyncFilterBuilder(new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3)]));
  }

  /// <summary>
  /// Creates a filter for specific event types.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder ForEventTypes<T1, T2, T3, T4>() {
    return new SyncFilterBuilder(new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4)]));
  }

  /// <summary>
  /// Creates a filter for specific event types.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder ForEventTypes<T1, T2, T3, T4, T5>() {
    return new SyncFilterBuilder(new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5)]));
  }

  /// <summary>
  /// Creates a filter for specific event types.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <typeparam name="T6">The sixth event type to filter by.</typeparam>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder ForEventTypes<T1, T2, T3, T4, T5, T6>() {
    return new SyncFilterBuilder(new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6)]));
  }

  /// <summary>
  /// Creates a filter for specific event types.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <typeparam name="T6">The sixth event type to filter by.</typeparam>
  /// <typeparam name="T7">The seventh event type to filter by.</typeparam>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder ForEventTypes<T1, T2, T3, T4, T5, T6, T7>() {
    return new SyncFilterBuilder(new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7)]));
  }

  /// <summary>
  /// Creates a filter for specific event types.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <typeparam name="T6">The sixth event type to filter by.</typeparam>
  /// <typeparam name="T7">The seventh event type to filter by.</typeparam>
  /// <typeparam name="T8">The eighth event type to filter by.</typeparam>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder ForEventTypes<T1, T2, T3, T4, T5, T6, T7, T8>() {
    return new SyncFilterBuilder(new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7), typeof(T8)]));
  }

  /// <summary>
  /// Creates a filter for specific event types.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <typeparam name="T6">The sixth event type to filter by.</typeparam>
  /// <typeparam name="T7">The seventh event type to filter by.</typeparam>
  /// <typeparam name="T8">The eighth event type to filter by.</typeparam>
  /// <typeparam name="T9">The ninth event type to filter by.</typeparam>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder ForEventTypes<T1, T2, T3, T4, T5, T6, T7, T8, T9>() {
    return new SyncFilterBuilder(new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7), typeof(T8), typeof(T9)]));
  }

  /// <summary>
  /// Creates a filter for specific event types.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <typeparam name="T6">The sixth event type to filter by.</typeparam>
  /// <typeparam name="T7">The seventh event type to filter by.</typeparam>
  /// <typeparam name="T8">The eighth event type to filter by.</typeparam>
  /// <typeparam name="T9">The ninth event type to filter by.</typeparam>
  /// <typeparam name="T10">The tenth event type to filter by.</typeparam>
  /// <returns>A builder for further configuration.</returns>
  public static SyncFilterBuilder ForEventTypes<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>() {
    return new SyncFilterBuilder(new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7), typeof(T8), typeof(T9), typeof(T10)]));
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
