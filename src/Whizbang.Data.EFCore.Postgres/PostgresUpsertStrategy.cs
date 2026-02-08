namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// PostgreSQL upsert strategy for perspective rows (insert or update).
/// </summary>
/// <remarks>
/// <para>
/// This strategy performs upserts (insert if not exists, update if exists) by:
/// 1. Querying for the existing row
/// 2. If not found: Insert via Add()
/// 3. If found: Update via Remove() then Add() (workaround for owned types)
/// 4. Save changes
/// 5. CRITICAL: Call ChangeTracker.Clear() to prevent entity tracking conflicts
/// </para>
/// <para>
/// <strong>Why remove-then-add for updates?</strong>
/// EF Core's owned types (like PerspectiveMetadata, PerspectiveScope) don't update cleanly with direct modification.
/// The remove-then-add pattern ensures a clean replacement of the entire row including owned properties.
/// </para>
/// <para>
/// <strong>Why ChangeTracker.Clear() is essential:</strong>
/// - The same DbContext is reused across multiple upsert operations (scoped per worker loop)
/// - After Remove() + SaveChangesAsync(), EF Core still tracks the deleted entity
/// - Without clearing, subsequent Add() with the same ID will throw tracking conflicts
/// </para>
/// <para>
/// Future optimization: Native ON CONFLICT when EF Core adds support. When we add Dapper/Npgsql implementations,
/// those can use native ON CONFLICT for true single-roundtrip upserts.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PhysicalFieldUpsertStrategyTests.cs:UpsertWithPhysicalFields_PostgresStrategy_SetsShadowPropertiesAsync</tests>
public class PostgresUpsertStrategy : BaseUpsertStrategy {
  /// <summary>
  /// PostgreSQL requires clearing the change tracker to prevent entity tracking conflicts
  /// when the same DbContext is reused across multiple upsert operations.
  /// </summary>
  protected override bool ClearChangeTrackerAfterSave => true;
}
