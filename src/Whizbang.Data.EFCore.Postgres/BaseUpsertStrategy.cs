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
    var existingRow = await context.Set<PerspectiveRow<TModel>>()
        .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    var now = DateTime.UtcNow;

    var row = existingRow == null
        ? _createNewRow(id, model, metadata, scope, now)
        : _createUpdatedRow(existingRow, model, metadata, scope, now);

    if (existingRow != null) {
      context.Set<PerspectiveRow<TModel>>().Remove(existingRow);
    }

    context.Set<PerspectiveRow<TModel>>().Add(row);

    if (physicalFieldValues != null) {
      var entry = context.Entry(row);
      foreach (var (columnName, value) in physicalFieldValues) {
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

  private static PerspectiveRow<TModel> _createUpdatedRow<TModel>(
      PerspectiveRow<TModel> existing, TModel model, PerspectiveMetadata metadata, PerspectiveScope scope, DateTime now)
      where TModel : class =>
    new() {
      Id = existing.Id,
      Data = model,
      Metadata = CloneMetadata(metadata),
      Scope = CloneScope(scope),
      CreatedAt = existing.CreatedAt,
      UpdatedAt = now,
      Version = existing.Version + 1
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
