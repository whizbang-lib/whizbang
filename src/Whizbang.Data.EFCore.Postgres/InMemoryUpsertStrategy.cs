using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core InMemory provider upsert strategy using query-then-save pattern.
/// Used for fast, isolated testing. Not for production use.
/// </summary>
public class InMemoryUpsertStrategy : IDbUpsertStrategy {

  /// <inheritdoc/>
  public async Task UpsertPerspectiveRowAsync<TModel>(
      DbContext context,
      string tableName,
      string id,
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
        Metadata = CloneMetadata(metadata),
        Scope = CloneScope(scope),
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
        Metadata = CloneMetadata(metadata),
        Scope = CloneScope(scope),
        CreatedAt = existingRow.CreatedAt, // Preserve creation time
        UpdatedAt = now,
        Version = existingRow.Version + 1
      };

      context.Set<PerspectiveRow<TModel>>().Add(updatedRow);
    }

    await context.SaveChangesAsync(cancellationToken);
  }

  /// <summary>
  /// Creates a clone of PerspectiveMetadata to avoid EF Core tracking issues.
  /// </summary>
  private static PerspectiveMetadata CloneMetadata(PerspectiveMetadata metadata) {
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
  private static PerspectiveScope CloneScope(PerspectiveScope scope) {
    return new PerspectiveScope {
      TenantId = scope.TenantId,
      CustomerId = scope.CustomerId,
      UserId = scope.UserId,
      OrganizationId = scope.OrganizationId
    };
  }
}
