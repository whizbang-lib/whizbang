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
      var newRow = new PerspectiveRow<TModel> {
        Id = id,
        Data = model,
        Metadata = CloneMetadata(metadata),
        Scope = CloneScope(scope),
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1
      };

      context.Set<PerspectiveRow<TModel>>().Add(newRow);
    } else {
      context.Set<PerspectiveRow<TModel>>().Remove(existingRow);

      var updatedRow = new PerspectiveRow<TModel> {
        Id = existingRow.Id,
        Data = model,
        Metadata = CloneMetadata(metadata),
        Scope = CloneScope(scope),
        CreatedAt = existingRow.CreatedAt,
        UpdatedAt = now,
        Version = existingRow.Version + 1
      };

      context.Set<PerspectiveRow<TModel>>().Add(updatedRow);
    }

    await context.SaveChangesAsync(cancellationToken);

    if (ClearChangeTrackerAfterSave) {
      context.ChangeTracker.Clear();
    }
  }

  /// <inheritdoc/>
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
      row = new PerspectiveRow<TModel> {
        Id = id,
        Data = model,
        Metadata = CloneMetadata(metadata),
        Scope = CloneScope(scope),
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1
      };

      context.Set<PerspectiveRow<TModel>>().Add(row);
    } else {
      context.Set<PerspectiveRow<TModel>>().Remove(existingRow);

      row = new PerspectiveRow<TModel> {
        Id = existingRow.Id,
        Data = model,
        Metadata = CloneMetadata(metadata),
        Scope = CloneScope(scope),
        CreatedAt = existingRow.CreatedAt,
        UpdatedAt = now,
        Version = existingRow.Version + 1
      };

      context.Set<PerspectiveRow<TModel>>().Add(row);
    }

    var entry = context.Entry(row);
    foreach (var (columnName, value) in physicalFieldValues) {
      entry.Property(columnName).CurrentValue = value;
    }

    await context.SaveChangesAsync(cancellationToken);

    if (ClearChangeTrackerAfterSave) {
      context.ChangeTracker.Clear();
    }
  }

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
