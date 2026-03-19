namespace Whizbang.Core.Lenses;

/// <summary>
/// Read-only query interface for temporal (append-only) perspectives.
/// Provides time-travel and activity feed queries aligned with EF Core temporal table patterns.
/// </summary>
/// <typeparam name="TModel">The log entry model type to query</typeparam>
/// <docs>fundamentals/lenses/temporal-query</docs>
/// <tests>tests/Whizbang.Core.Tests/Lenses/ITemporalLensQueryTests.cs</tests>
/// <remarks>
/// <para>
/// This interface follows the query patterns established by EF Core for SQL Server temporal tables.
/// See: https://learn.microsoft.com/en-us/ef/core/providers/sql-server/temporal-tables
/// </para>
/// <para>
/// <strong>Time Concepts:</strong>
/// <list type="bullet">
///   <item><description>System time (PeriodStart/PeriodEnd) - When the database recorded the change</description></item>
///   <item><description>Valid time (ValidTime) - When the business event occurred</description></item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Get full history for an order
/// var history = await temporalLens
///     .TemporalAll()
///     .Where(r => r.StreamId == orderId)
///     .OrderBy(r => r.PeriodStart)
///     .ToListAsync();
///
/// // Get state as of last week
/// var lastWeekState = await temporalLens
///     .TemporalAsOf(DateTimeOffset.UtcNow.AddDays(-7))
///     .ToListAsync();
///
/// // Get recent activity for a user
/// var activity = await temporalLens
///     .RecentActivityForUser(userId, limit: 20)
///     .ToListAsync();
/// </code>
/// </example>
public interface ITemporalLensQuery<TModel> : ILensQuery where TModel : class {
  /// <summary>
  /// All temporal rows including full history.
  /// Returns all Insert/Update/Delete entries ever recorded.
  /// Equivalent to EF Core's TemporalAll() for SQL Server temporal tables.
  /// </summary>
  /// <returns>Queryable of all temporal rows</returns>
  IQueryable<TemporalPerspectiveRow<TModel>> TemporalAll();

  /// <summary>
  /// Latest state per stream.
  /// Returns only the most recent row for each StreamId based on PeriodStart.
  /// For streams with ActionType=Delete, returns the Delete row (caller can filter if needed).
  /// </summary>
  /// <returns>Queryable with one row per stream (the latest)</returns>
  IQueryable<TemporalPerspectiveRow<TModel>> LatestPerStream();

  /// <summary>
  /// State as of a specific point in time.
  /// Returns rows that were active (current) at the given UTC time.
  /// Equivalent to EF Core's TemporalAsOf(DateTime).
  /// </summary>
  /// <param name="systemTime">The point in time to query</param>
  /// <returns>Queryable of rows that were current at the specified time</returns>
  IQueryable<TemporalPerspectiveRow<TModel>> TemporalAsOf(DateTimeOffset systemTime);

  /// <summary>
  /// Rows that were active between two given UTC times.
  /// Returns rows where the period [PeriodStart, PeriodEnd) overlaps with [start, end).
  /// Equivalent to EF Core's TemporalFromTo(start, end).
  /// </summary>
  /// <param name="startTime">Start of the time range (inclusive)</param>
  /// <param name="endTime">End of the time range (exclusive)</param>
  /// <returns>Queryable of rows that were active during the specified range</returns>
  IQueryable<TemporalPerspectiveRow<TModel>> TemporalFromTo(DateTimeOffset startTime, DateTimeOffset endTime);

  /// <summary>
  /// Rows that started AND ended within the given time range.
  /// Returns rows where PeriodStart >= start AND PeriodEnd &lt;= end.
  /// Equivalent to EF Core's TemporalContainedIn(start, end).
  /// </summary>
  /// <param name="startTime">Start of the time range (inclusive)</param>
  /// <param name="endTime">End of the time range (inclusive)</param>
  /// <returns>Queryable of rows fully contained within the specified range</returns>
  IQueryable<TemporalPerspectiveRow<TModel>> TemporalContainedIn(DateTimeOffset startTime, DateTimeOffset endTime);

  /// <summary>
  /// Recent activity for a specific stream (most recent first).
  /// Convenience method for common activity feed pattern.
  /// </summary>
  /// <param name="streamId">The stream (aggregate) ID to get activity for</param>
  /// <param name="limit">Maximum number of entries to return (default: 50)</param>
  /// <returns>Queryable of recent activity, ordered by ValidTime descending</returns>
  IQueryable<TemporalPerspectiveRow<TModel>> RecentActivityForStream(Guid streamId, int limit = 50);

  /// <summary>
  /// Recent activity for a specific user (most recent first).
  /// Convenience method for user activity feed pattern.
  /// Queries based on Scope.UserId.
  /// </summary>
  /// <param name="userId">The user ID to get activity for</param>
  /// <param name="limit">Maximum number of entries to return (default: 50)</param>
  /// <returns>Queryable of recent activity, ordered by ValidTime descending</returns>
  IQueryable<TemporalPerspectiveRow<TModel>> RecentActivityForUser(string userId, int limit = 50);
}
