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
    // Check if entity exists in local tracker and detach it to avoid tracking conflicts.
    // EF Core 10's ComplexProperty().ToJson() maintains internal indexes for collections
    // inside complex types. Any modification to tracked complex type collections corrupts
    // these indexes, causing ArgumentOutOfRangeException during AcceptChanges.
    // By detaching tracked entities and using AsNoTracking() for queries, we ensure
    // no corrupted tracking state exists when we attach the updated entity.
    var localRow = context.Set<PerspectiveRow<TModel>>().Local
        .FirstOrDefault(r => r.Id == id);

    if (localRow != null) {
      context.Entry(localRow).State = EntityState.Detached;
    }

    // Query database WITHOUT tracking to get a clean entity with no internal tracking state.
    var existingRow = await context.Set<PerspectiveRow<TModel>>()
        .AsNoTracking()
        .OrderBy(r => r.Id)
        .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    var now = DateTime.UtcNow;

    PerspectiveRow<TModel> row;
    if (existingRow != null) {
      // Create updated row with new complex type instances.
      // We use Update() to attach as Modified, which will update all columns.
      row = new PerspectiveRow<TModel> {
        Id = existingRow.Id,
        Data = model,
        Metadata = CloneMetadata(metadata),
        Scope = CloneScope(existingRow.Scope),
        CreatedAt = existingRow.CreatedAt,
        UpdatedAt = now,
        Version = existingRow.Version + 1
      };
      context.Set<PerspectiveRow<TModel>>().Update(row);
      // SECURITY: Exclude scope from UPDATE SQL. Scope is set only on INSERT.
      context.Entry(row).ComplexProperty(e => e.Scope).IsModified = false;
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
      AllowedPrincipals = [.. (scope.AllowedPrincipals ?? [])],
      Extensions = [.. (scope.Extensions ?? [])]
    };
  }

  /// <summary>
  /// Updates PerspectiveMetadata in place for tracked entities.
  /// Required for EF Core 10 ComplexProperty().ToJson() to avoid index corruption.
  /// </summary>
  /// <docs>data/efcore-complex-types#in-place-updates</docs>
  /// <tests>Whizbang.Data.EFCore.Postgres.Tests/BaseUpsertStrategyInPlaceUpdateTests.cs</tests>
  protected static void UpdateMetadataInPlace(PerspectiveMetadata target, PerspectiveMetadata source) {
    target.EventType = source.EventType;
    target.EventId = source.EventId;
    target.Timestamp = source.Timestamp;
    target.CorrelationId = source.CorrelationId;
    target.CausationId = source.CausationId;
  }

  /// <summary>
  /// Updates PerspectiveScope in place for tracked entities.
  /// CRITICAL: Must clear and re-add collection items, NOT replace the List instances.
  /// EF Core 10's InternalComplexCollectionEntry maintains indexes into collections.
  /// Replacing List instances corrupts those indexes causing ArgumentOutOfRangeException.
  /// </summary>
  /// <docs>data/efcore-complex-types#in-place-updates</docs>
  /// <tests>Whizbang.Data.EFCore.Postgres.Tests/BaseUpsertStrategyInPlaceUpdateTests.cs</tests>
  protected static void UpdateScopeInPlace(PerspectiveScope target, PerspectiveScope source) {
    target.TenantId = source.TenantId;
    target.CustomerId = source.CustomerId;
    target.UserId = source.UserId;
    target.OrganizationId = source.OrganizationId;

    // Clear and re-add collection items - DO NOT replace the List instances
    target.AllowedPrincipals.Clear();
    foreach (var principal in source.AllowedPrincipals) {
      target.AllowedPrincipals.Add(principal);
    }

    target.Extensions.Clear();
    foreach (var extension in source.Extensions) {
      target.Extensions.Add(new ScopeExtension(extension.Key, extension.Value));
    }
  }
}
