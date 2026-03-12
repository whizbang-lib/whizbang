namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Fluent builder for creating perspective sync filters.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// // Simple filter
/// var options = SyncFilter.ForStream(orderId).Build();
///
/// // Complex AND/OR combination
/// var options = SyncFilter.ForStream(orderId)
///     .AndEventTypes&lt;OrderCreatedEvent&gt;()
///     .Or(SyncFilter.ForEventTypes&lt;OrderCancelledEvent&gt;())
///     .WithTimeout(TimeSpan.FromSeconds(10))
///     .Build();
/// </code>
/// <para>
/// All synchronization uses database-based lookup. The database is the only
/// authority for determining when perspectives have processed events.
/// </para>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
public sealed class SyncFilterBuilder {
  private SyncFilterNode _filter;
  private TimeSpan _timeout = TimeSpan.FromSeconds(5);
  private readonly bool _debuggerAwareTimeout = true;

  internal SyncFilterBuilder(SyncFilterNode filter) {
    _filter = filter;
  }

  // ==========================================================================
  // AND combinators
  // ==========================================================================

  /// <summary>
  /// Combines this filter with another using AND logic.
  /// </summary>
  /// <param name="other">The other filter builder to combine with.</param>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder And(SyncFilterBuilder other) {
    ArgumentNullException.ThrowIfNull(other);
    _filter = new AndFilter(_filter, other._filter);
    return this;
  }

  /// <summary>
  /// Adds a stream filter with AND logic.
  /// </summary>
  /// <param name="streamId">The stream ID to filter by.</param>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndStream(Guid streamId) {
    _filter = new AndFilter(_filter, new StreamFilter(streamId));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with AND logic.
  /// </summary>
  /// <typeparam name="T">The event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndEventTypes<T>() {
    _filter = new AndFilter(_filter, new EventTypeFilter([typeof(T)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with AND logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndEventTypes<T1, T2>() {
    _filter = new AndFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with AND logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndEventTypes<T1, T2, T3>() {
    _filter = new AndFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with AND logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndEventTypes<T1, T2, T3, T4>() {
    _filter = new AndFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with AND logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndEventTypes<T1, T2, T3, T4, T5>() {
    _filter = new AndFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with AND logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <typeparam name="T6">The sixth event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndEventTypes<T1, T2, T3, T4, T5, T6>() {
    _filter = new AndFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with AND logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <typeparam name="T6">The sixth event type to filter by.</typeparam>
  /// <typeparam name="T7">The seventh event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndEventTypes<T1, T2, T3, T4, T5, T6, T7>() {
    _filter = new AndFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with AND logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <typeparam name="T6">The sixth event type to filter by.</typeparam>
  /// <typeparam name="T7">The seventh event type to filter by.</typeparam>
  /// <typeparam name="T8">The eighth event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndEventTypes<T1, T2, T3, T4, T5, T6, T7, T8>() {
    _filter = new AndFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7), typeof(T8)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with AND logic.
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
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndEventTypes<T1, T2, T3, T4, T5, T6, T7, T8, T9>() {
    _filter = new AndFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7), typeof(T8), typeof(T9)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with AND logic.
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
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndEventTypes<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>() {
    _filter = new AndFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7), typeof(T8), typeof(T9), typeof(T10)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with AND logic.
  /// </summary>
  /// <param name="eventTypes">The event types to filter by.</param>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndEventTypes(params Type[] eventTypes) {
    ArgumentNullException.ThrowIfNull(eventTypes);
    _filter = new AndFilter(_filter, new EventTypeFilter(eventTypes));
    return this;
  }

  /// <summary>
  /// Adds a current scope filter with AND logic.
  /// </summary>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder AndCurrentScope() {
    _filter = new AndFilter(_filter, new CurrentScopeFilter());
    return this;
  }

  // ==========================================================================
  // OR combinators
  // ==========================================================================

  /// <summary>
  /// Combines this filter with another using OR logic.
  /// </summary>
  /// <param name="other">The other filter builder to combine with.</param>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder Or(SyncFilterBuilder other) {
    ArgumentNullException.ThrowIfNull(other);
    _filter = new OrFilter(_filter, other._filter);
    return this;
  }

  /// <summary>
  /// Adds a stream filter with OR logic.
  /// </summary>
  /// <param name="streamId">The stream ID to filter by.</param>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder OrStream(Guid streamId) {
    _filter = new OrFilter(_filter, new StreamFilter(streamId));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with OR logic.
  /// </summary>
  /// <typeparam name="T">The event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder OrEventTypes<T>() {
    _filter = new OrFilter(_filter, new EventTypeFilter([typeof(T)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with OR logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder OrEventTypes<T1, T2>() {
    _filter = new OrFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with OR logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder OrEventTypes<T1, T2, T3>() {
    _filter = new OrFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with OR logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder OrEventTypes<T1, T2, T3, T4>() {
    _filter = new OrFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with OR logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder OrEventTypes<T1, T2, T3, T4, T5>() {
    _filter = new OrFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with OR logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <typeparam name="T6">The sixth event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder OrEventTypes<T1, T2, T3, T4, T5, T6>() {
    _filter = new OrFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with OR logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <typeparam name="T6">The sixth event type to filter by.</typeparam>
  /// <typeparam name="T7">The seventh event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder OrEventTypes<T1, T2, T3, T4, T5, T6, T7>() {
    _filter = new OrFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with OR logic.
  /// </summary>
  /// <typeparam name="T1">The first event type to filter by.</typeparam>
  /// <typeparam name="T2">The second event type to filter by.</typeparam>
  /// <typeparam name="T3">The third event type to filter by.</typeparam>
  /// <typeparam name="T4">The fourth event type to filter by.</typeparam>
  /// <typeparam name="T5">The fifth event type to filter by.</typeparam>
  /// <typeparam name="T6">The sixth event type to filter by.</typeparam>
  /// <typeparam name="T7">The seventh event type to filter by.</typeparam>
  /// <typeparam name="T8">The eighth event type to filter by.</typeparam>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder OrEventTypes<T1, T2, T3, T4, T5, T6, T7, T8>() {
    _filter = new OrFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7), typeof(T8)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with OR logic.
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
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder OrEventTypes<T1, T2, T3, T4, T5, T6, T7, T8, T9>() {
    _filter = new OrFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7), typeof(T8), typeof(T9)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with OR logic.
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
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder OrEventTypes<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>() {
    _filter = new OrFilter(_filter, new EventTypeFilter([typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7), typeof(T8), typeof(T9), typeof(T10)]));
    return this;
  }

  /// <summary>
  /// Adds an event type filter with OR logic.
  /// </summary>
  /// <param name="eventTypes">The event types to filter by.</param>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder OrEventTypes(params Type[] eventTypes) {
    ArgumentNullException.ThrowIfNull(eventTypes);
    _filter = new OrFilter(_filter, new EventTypeFilter(eventTypes));
    return this;
  }

  // ==========================================================================
  // Timeout configuration
  // ==========================================================================

  /// <summary>
  /// Sets the timeout duration for synchronization.
  /// </summary>
  /// <param name="timeout">The timeout duration.</param>
  /// <returns>This builder for chaining.</returns>
  public SyncFilterBuilder WithTimeout(TimeSpan timeout) {
    _timeout = timeout;
    return this;
  }

  // ==========================================================================
  // Build
  // ==========================================================================

  /// <summary>
  /// Builds the <see cref="PerspectiveSyncOptions"/> from the current builder state.
  /// </summary>
  /// <returns>The configured options.</returns>
  public PerspectiveSyncOptions Build() {
    return new PerspectiveSyncOptions {
      Filter = _filter,
      Timeout = _timeout,
      DebuggerAwareTimeout = _debuggerAwareTimeout
    };
  }

  /// <summary>
  /// Implicitly converts a builder to <see cref="PerspectiveSyncOptions"/>.
  /// </summary>
  /// <param name="builder">The builder to convert.</param>
  public static implicit operator PerspectiveSyncOptions(SyncFilterBuilder builder) {
    ArgumentNullException.ThrowIfNull(builder);
    return builder.Build();
  }
}
