using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

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
/// <tests>No tests found</tests>
public class PostgresUpsertStrategy : IDbUpsertStrategy {

  /// <inheritdoc/>
  /// <tests>No tests found</tests>
  public async Task UpsertPerspectiveRowAsync<TModel>(
      DbContext context,
      string tableName,
      Guid id,
      TModel model,
      PerspectiveMetadata metadata,
      PerspectiveScope scope,
      CancellationToken cancellationToken = default)
      where TModel : class {

    var existingRow = await context.Set<PerspectiveRow<TModel>>()
        .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    var now = DateTime.UtcNow;

    if (existingRow == null) {
      // Insert new record
      var newRow = new PerspectiveRow<TModel> {
        Id = id,
        Data = model,
        Metadata = _cloneMetadata(metadata),
        Scope = _cloneScope(scope),
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1
      };

      context.Set<PerspectiveRow<TModel>>().Add(newRow);
    } else {
      // Update existing record - remove and re-add to handle owned types properly
      context.Set<PerspectiveRow<TModel>>().Remove(existingRow);

      var updatedRow = new PerspectiveRow<TModel> {
        Id = existingRow.Id,
        Data = model,
        Metadata = _cloneMetadata(metadata),
        Scope = _cloneScope(scope),
        CreatedAt = existingRow.CreatedAt, // Preserve creation time
        UpdatedAt = now,
        Version = existingRow.Version + 1
      };

      context.Set<PerspectiveRow<TModel>>().Add(updatedRow);
    }

    await context.SaveChangesAsync(cancellationToken);

    // CRITICAL: Clear change tracker to prevent entity tracking conflicts
    // The remove-then-add pattern leaves the deleted entity tracked by EF Core.
    // When the same DbContext is reused (scoped per worker loop), subsequent upserts
    // with the same ID will fail with tracking conflicts unless we clear the tracker.
    context.ChangeTracker.Clear();
  }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PhysicalFieldUpsertStrategyTests.cs:UpsertWithPhysicalFields_PostgresStrategy_SetsShadowPropertiesAsync</tests>
  public async Task UpsertPerspectiveRowWithPhysicalFieldsAsync<TModel>(
      DbContext context,
      string tableName,
      Guid id,
      TModel model,
      PerspectiveMetadata metadata,
      PerspectiveScope scope,
      IDictionary<string, object?> physicalFieldValues,
      CancellationToken cancellationToken = default)
      where TModel : class {

    var existingRow = await context.Set<PerspectiveRow<TModel>>()
        .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    var now = DateTime.UtcNow;
    PerspectiveRow<TModel> row;

    if (existingRow == null) {
      // Insert new record
      row = new PerspectiveRow<TModel> {
        Id = id,
        Data = model,
        Metadata = _cloneMetadata(metadata),
        Scope = _cloneScope(scope),
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1
      };

      context.Set<PerspectiveRow<TModel>>().Add(row);
    } else {
      // Update existing record - remove and re-add to handle owned types properly
      context.Set<PerspectiveRow<TModel>>().Remove(existingRow);

      row = new PerspectiveRow<TModel> {
        Id = existingRow.Id,
        Data = model,
        Metadata = _cloneMetadata(metadata),
        Scope = _cloneScope(scope),
        CreatedAt = existingRow.CreatedAt, // Preserve creation time
        UpdatedAt = now,
        Version = existingRow.Version + 1
      };

      context.Set<PerspectiveRow<TModel>>().Add(row);
    }

    // Set shadow property values for physical fields
    var entry = context.Entry(row);
    foreach (var (columnName, value) in physicalFieldValues) {
      entry.Property(columnName).CurrentValue = value;
    }

    await context.SaveChangesAsync(cancellationToken);

    // CRITICAL: Clear change tracker to prevent entity tracking conflicts
    context.ChangeTracker.Clear();
  }

  /// <summary>
  /// Creates a clone of PerspectiveMetadata to avoid EF Core tracking issues.
  /// </summary>
  /// <tests>No tests found</tests>
  private static PerspectiveMetadata _cloneMetadata(PerspectiveMetadata metadata) {
    return new PerspectiveMetadata {
      EventType = metadata.EventType,
      EventId = metadata.EventId,
      Timestamp = metadata.Timestamp,
      CorrelationId = metadata.CorrelationId,
      CausationId = metadata.CausationId
    };
  }

  /// <summary>
  /// Creates a clone of PerspectiveScope to avoid EF Core tracking issues.
  /// </summary>
  /// <tests>No tests found</tests>
  private static PerspectiveScope _cloneScope(PerspectiveScope scope) {
    return new PerspectiveScope {
      TenantId = scope.TenantId,
      CustomerId = scope.CustomerId,
      UserId = scope.UserId,
      OrganizationId = scope.OrganizationId,
      AllowedPrincipals = [.. scope.AllowedPrincipals],
      // Extensions as List<ScopeExtension> for EF Core ComplexProperty().ToJson() compatibility
      Extensions = [.. scope.Extensions]
    };
  }
}
