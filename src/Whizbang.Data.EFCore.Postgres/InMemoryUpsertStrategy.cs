using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core InMemory provider upsert strategy using query-then-save pattern.
/// Used for fast, isolated testing. Not for production use.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:Constructor_WithNullTableName_ThrowsArgumentNullExceptionAsync</tests>
public class InMemoryUpsertStrategy : IDbUpsertStrategy {

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync</tests>
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
  }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PhysicalFieldUpsertStrategyTests.cs:UpsertWithPhysicalFields_WhenRecordDoesNotExist_CreatesShadowPropertiesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PhysicalFieldUpsertStrategyTests.cs:UpsertWithPhysicalFields_WhenRecordExists_UpdatesShadowPropertiesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PhysicalFieldUpsertStrategyTests.cs:UpsertWithPhysicalFields_WithNullValues_SetsShadowPropertiesToNullAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PhysicalFieldUpsertStrategyTests.cs:UpsertWithPhysicalFields_WithEmptyDictionary_DoesNotFailAsync</tests>
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
  }

  /// <summary>
  /// Creates a clone of PerspectiveMetadata to avoid EF Core tracking issues.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync</tests>
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
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync</tests>
  private static PerspectiveScope _cloneScope(PerspectiveScope scope) {
    return new PerspectiveScope {
      TenantId = scope.TenantId,
      CustomerId = scope.CustomerId,
      UserId = scope.UserId,
      OrganizationId = scope.OrganizationId,
      AllowedPrincipals = scope.AllowedPrincipals?.ToList() ?? [],
      // Extensions as List<ScopeExtension> for EF Core ComplexProperty().ToJson() compatibility
      Extensions = scope.Extensions?.Select(e => new ScopeExtension(e.Key, e.Value)).ToList() ?? []
    };
  }
}
