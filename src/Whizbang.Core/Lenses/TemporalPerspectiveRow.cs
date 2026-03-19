using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Lenses;

/// <summary>
/// Row from a temporal (append-only) perspective table.
/// Each row represents a single event transformation at a point in time.
/// Follows patterns from SQL Server temporal tables and EF Core's temporal support.
/// </summary>
/// <typeparam name="TModel">The log entry model type</typeparam>
/// <docs>fundamentals/lenses/temporal-query</docs>
/// <tests>tests/Whizbang.Core.Tests/Lenses/TemporalPerspectiveRowTests.cs</tests>
/// <remarks>
/// <para>
/// Unlike <see cref="PerspectiveRow{TModel}"/> which maintains a single row per stream (UPSERT),
/// <see cref="TemporalPerspectiveRow{TModel}"/> creates a new row for each event (INSERT).
/// This enables full history tracking, time-travel queries, and activity feeds.
/// </para>
/// <para>
/// <strong>Temporal Fields (aligned with SQL Server patterns):</strong>
/// <list type="bullet">
///   <item><description><see cref="PeriodStart"/> - When this version became active (SysStartTime)</description></item>
///   <item><description><see cref="PeriodEnd"/> - When this version was superseded (SysEndTime)</description></item>
///   <item><description><see cref="ValidTime"/> - Business time from the event</description></item>
///   <item><description><see cref="ActionType"/> - Insert/Update/Delete action</description></item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Query recent activity for a user
/// var activity = await temporalLens
///     .TemporalAll()
///     .Where(r => r.Scope.UserId == userId)
///     .OrderByDescending(r => r.ValidTime)
///     .Take(20)
///     .ToListAsync();
/// </code>
/// </example>
public class TemporalPerspectiveRow<TModel> where TModel : class {
  /// <summary>
  /// Unique identifier for this temporal row.
  /// Typically a UUIDv7 for time-ordering within the table.
  /// </summary>
  public required Guid Id { get; init; }

  /// <summary>
  /// Stream ID (aggregate ID) this entry belongs to.
  /// Multiple rows can share the same StreamId (one per event).
  /// </summary>
  public required Guid StreamId { get; init; }

  /// <summary>
  /// The event ID that created this temporal entry.
  /// Tracks which specific event was transformed to create this row.
  /// </summary>
  public required Guid EventId { get; init; }

  /// <summary>
  /// The transformed log entry data.
  /// Stored as JSONB in PostgreSQL, JSON in SQL Server.
  /// </summary>
  public required TModel Data { get; init; }

  /// <summary>
  /// Event metadata (event type, correlation, causation, timestamp).
  /// Same pattern as <see cref="PerspectiveRow{TModel}.Metadata"/>.
  /// </summary>
  /// <remarks>
  /// Uses <c>set</c> accessor for EF Core ComplexProperty materialization compatibility.
  /// </remarks>
  public required PerspectiveMetadata Metadata { get; set; }

  /// <summary>
  /// Multi-tenancy and security scope (tenant ID, user ID, org ID).
  /// Same pattern as <see cref="PerspectiveRow{TModel}.Scope"/>.
  /// </summary>
  /// <remarks>
  /// Uses <c>set</c> accessor for EF Core OwnsOne/ComplexProperty materialization compatibility.
  /// </remarks>
  public required PerspectiveScope Scope { get; set; }

  /// <summary>
  /// The type of action that created this entry.
  /// Indicates whether this was an Insert, Update, or Delete.
  /// </summary>
  public required TemporalActionType ActionType { get; init; }

  /// <summary>
  /// When this row became active in the database (system time).
  /// Equivalent to SQL Server's SysStartTime in temporal tables.
  /// </summary>
  /// <remarks>
  /// This is the database record time, not the business event time.
  /// Use <see cref="ValidTime"/> for business time from the event.
  /// </remarks>
  public required DateTime PeriodStart { get; init; }

  /// <summary>
  /// When this row was superseded by a newer version (system time).
  /// For current/active rows, this is <see cref="DateTime.MaxValue"/>.
  /// Equivalent to SQL Server's SysEndTime in temporal tables.
  /// </summary>
  public required DateTime PeriodEnd { get; init; }

  /// <summary>
  /// Business time from the event that created this entry.
  /// This is the time the action occurred in business terms.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Distinction between system time and business time:
  /// <list type="bullet">
  ///   <item><description><see cref="PeriodStart"/>/<see cref="PeriodEnd"/> - When we recorded it (system time)</description></item>
  ///   <item><description><see cref="ValidTime"/> - When it happened in business terms (event time)</description></item>
  /// </list>
  /// </para>
  /// <para>
  /// This enables bi-temporal queries where you need to know both
  /// when something happened and when we knew about it.
  /// </para>
  /// </remarks>
  public required DateTimeOffset ValidTime { get; init; }
}
