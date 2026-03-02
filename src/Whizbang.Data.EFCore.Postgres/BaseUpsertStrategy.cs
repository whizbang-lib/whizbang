using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Base class for upsert strategies containing shared implementation logic.
/// </summary>
public abstract class BaseUpsertStrategy : IDbUpsertStrategy {
  /// <summary>
  /// When true, clears the change tracker after save to prevent entity tracking conflicts.
  /// Override in derived classes based on provider requirements.
  /// </summary>
  protected virtual bool ClearChangeTrackerAfterSave => false;

  /// <inheritdoc/>
  public Task UpsertPerspectiveRowAsync<TModel>(
      DbContext context,
      string tableName,
      Guid id,
      TModel model,
      PerspectiveMetadata metadata,
      PerspectiveScope scope,
      CancellationToken cancellationToken = default)
      where TModel : class =>
    _upsertCoreAsync(context, id, model, metadata, scope, null, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertPerspectiveRowWithPhysicalFieldsAsync<TModel>(
      DbContext context,
      string tableName,
      Guid id,
      TModel model,
      PerspectiveMetadata metadata,
      PerspectiveScope scope,
      IDictionary<string, object?> physicalFieldValues,
      CancellationToken cancellationToken = default)
      where TModel : class =>
    _upsertCoreAsync(context, id, model, metadata, scope, physicalFieldValues, cancellationToken);

  private async Task _upsertCoreAsync<TModel>(
      DbContext context,
      Guid id,
      TModel model,
      PerspectiveMetadata metadata,
      PerspectiveScope scope,
      IDictionary<string, object?>? physicalFieldValues,
      CancellationToken cancellationToken)
      where TModel : class {
    // First check local change tracker to avoid tracking conflicts.
    // This handles cases where the same entity is processed multiple times
    // in a batch before SaveChanges clears the tracker.
    var existingRow = context.Set<PerspectiveRow<TModel>>().Local
        .FirstOrDefault(r => r.Id == id);

    // If not found locally, query the database
    existingRow ??= await context.Set<PerspectiveRow<TModel>>()
        .AsTracking()
        .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    var now = DateTime.UtcNow;

    PerspectiveRow<TModel> row;
    if (existingRow != null) {
      // Update existing row in place to avoid EF Core tracking issues with complex type collections.
      // The previous remove+add pattern caused ArgumentOutOfRangeException when EF Core tried to
      // track nested collection changes during shared identity handling.
      existingRow.Data = model;
      existingRow.Metadata = CloneMetadata(metadata);
      existingRow.Scope = CloneScope(scope);
      existingRow.UpdatedAt = now;
      existingRow.Version++;

      // Force-mark Data as modified for polymorphic models where EF Core uses reference equality.
      // Apply methods commonly mutate in place and return the same reference, which EF Core
      // won't detect as a change. This ensures the JSONB data column is always included in UPDATEs.
      context.Entry(existingRow).Property(e => e.Data).IsModified = true;

      row = existingRow;
    } else {
      row = _createNewRow(id, model, metadata, scope, now);
      context.Set<PerspectiveRow<TModel>>().Add(row);
    }

    if (physicalFieldValues != null) {
      var entry = context.Entry(row);
      foreach (var (columnName, value) in physicalFieldValues) {
        // Values should already be the correct type (Vector, not float[])
        // The source generator converts float[] to Vector at compile time
        entry.Property(columnName).CurrentValue = value;
      }
    }

    await context.SaveChangesAsync(cancellationToken);

    if (ClearChangeTrackerAfterSave) {
      context.ChangeTracker.Clear();
    }
  }

  private static PerspectiveRow<TModel> _createNewRow<TModel>(
      Guid id, TModel model, PerspectiveMetadata metadata, PerspectiveScope scope, DateTime now)
      where TModel : class =>
    new() {
      Id = id,
      Data = model,
      Metadata = CloneMetadata(metadata),
      Scope = CloneScope(scope),
      CreatedAt = now,
      UpdatedAt = now,
      Version = 1
    };

  /// <summary>
  /// Creates a clone of PerspectiveMetadata to avoid EF Core tracking issues.
  /// </summary>
  protected static PerspectiveMetadata CloneMetadata(PerspectiveMetadata metadata) {
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
  protected static PerspectiveScope CloneScope(PerspectiveScope scope) {
    return new PerspectiveScope {
      TenantId = scope.TenantId,
      CustomerId = scope.CustomerId,
      UserId = scope.UserId,
      OrganizationId = scope.OrganizationId,
      AllowedPrincipals = [.. scope.AllowedPrincipals],
      Extensions = [.. scope.Extensions]
    };
  }
}
