namespace Whizbang.Core.Perspectives;

/// <summary>
/// Write-only abstraction for temporal (append-only) perspective data storage.
/// Unlike <see cref="IPerspectiveStore{TModel}"/> which uses UPSERT (update or insert),
/// this store always INSERTs new rows - it never updates existing rows.
/// </summary>
/// <typeparam name="TModel">The log entry model type to store</typeparam>
/// <docs>perspectives/temporal</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/ITemporalPerspectiveStoreTests.cs</tests>
/// <remarks>
/// <para>
/// Temporal perspectives create an append-only log of all events.
/// Each event transformation creates a new row with:
/// <list type="bullet">
///   <item><description>UUIDv7 ID for time-ordering</description></item>
///   <item><description>StreamId to identify the aggregate</description></item>
///   <item><description>EventId to track the source event</description></item>
///   <item><description>ValidTime for business time from the event</description></item>
///   <item><description>PeriodStart/PeriodEnd for system time tracking</description></item>
/// </list>
/// </para>
/// <para>
/// This store is used by <see cref="ITemporalPerspectiveFor{TModel, TEvent1}"/> implementations
/// via generated runners. The Transform method produces entries, and this store persists them.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Used by generated temporal perspective runners:
/// var entry = perspective.Transform(eventData);
/// if (entry != null) {
///   await store.AppendAsync(
///     streamId: eventData.OrderId,
///     eventId: envelope.EventId,
///     model: entry,
///     validTime: envelope.Timestamp,
///     cancellationToken);
/// }
/// </code>
/// </example>
#pragma warning disable S3246 // TModel used in both input and output positions — variance not applicable
public interface ITemporalPerspectiveStore<TModel> where TModel : class {
#pragma warning restore S3246
  /// <summary>
  /// Appends a new row to the temporal perspective table.
  /// Always INSERTs - never updates existing rows.
  /// </summary>
  /// <param name="streamId">Stream ID (aggregate ID) this entry belongs to</param>
  /// <param name="eventId">The event ID that created this entry</param>
  /// <param name="model">The transformed log entry data</param>
  /// <param name="validTime">Business time from the event (when it happened)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <remarks>
  /// <para>
  /// The implementation generates:
  /// <list type="bullet">
  ///   <item><description>A UUIDv7 ID for time-ordering within the table</description></item>
  ///   <item><description>PeriodStart = current UTC time (when we recorded it)</description></item>
  ///   <item><description>PeriodEnd = DateTime.MaxValue (currently active)</description></item>
  ///   <item><description>ActionType based on event semantics or explicit marking</description></item>
  /// </list>
  /// </para>
  /// </remarks>
  Task AppendAsync(
      Guid streamId,
      Guid eventId,
      TModel model,
      DateTimeOffset validTime,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Ensures all pending changes are committed to the database.
  /// Critical for PostPerspectiveInline lifecycle stage, which guarantees
  /// that perspective data is persisted and queryable before receptors fire.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <remarks>
  /// For EF Core implementations, this calls SaveChangesAsync() to commit the transaction.
  /// For other implementations (Dapper, raw SQL), this may be a no-op if changes are already committed.
  /// </remarks>
  Task FlushAsync(CancellationToken cancellationToken = default);
}
