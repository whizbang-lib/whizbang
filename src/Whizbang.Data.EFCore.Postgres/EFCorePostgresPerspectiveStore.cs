using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of <see cref="IPerspectiveStore{TModel}"/> for PostgreSQL.
/// Provides write operations for perspective data with automatic versioning and timestamp management.
/// </summary>
/// <typeparam name="TModel">The model type stored in the perspective</typeparam>
public class EFCorePostgresPerspectiveStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TModel> : IPerspectiveStore<TModel>
    where TModel : class {

  private readonly DbContext _context;
  private readonly string _tableName;

  /// <summary>
  /// Initializes a new instance of <see cref="EFCorePostgresPerspectiveStore{TModel}"/>.
  /// </summary>
  /// <param name="context">The EF Core DbContext</param>
  /// <param name="tableName">The table name for this perspective (for diagnostics/logging)</param>
  public EFCorePostgresPerspectiveStore(DbContext context, string tableName) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
    _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
  }

  /// <inheritdoc/>
  public async Task UpsertAsync(string id, TModel model, CancellationToken cancellationToken = default) {
    var existingRow = await _context.Set<PerspectiveRow<TModel>>()
        .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    if (existingRow == null) {
      // Insert new record
      var newRow = new PerspectiveRow<TModel> {
        Id = id,
        Data = model,
        Metadata = new PerspectiveMetadata {
          EventType = "Unknown",
          EventId = Guid.NewGuid().ToString(),
          Timestamp = DateTime.UtcNow
        },
        Scope = new PerspectiveScope(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Version = 1
      };

      _context.Set<PerspectiveRow<TModel>>().Add(newRow);
    } else {
      // Update existing record - remove and re-add to handle owned types properly
      _context.Set<PerspectiveRow<TModel>>().Remove(existingRow);

      var updatedRow = new PerspectiveRow<TModel> {
        Id = existingRow.Id,
        Data = model, // Replace entire data
        Metadata = CloneMetadata(existingRow.Metadata), // Clone metadata
        Scope = CloneScope(existingRow.Scope), // Clone scope
        CreatedAt = existingRow.CreatedAt, // Preserve creation time
        UpdatedAt = DateTime.UtcNow, // Update timestamp
        Version = existingRow.Version + 1 // Increment version
      };

      _context.Set<PerspectiveRow<TModel>>().Add(updatedRow);
    }

    await _context.SaveChangesAsync(cancellationToken);
  }

  /// <inheritdoc/>
  public async Task UpdateFieldsAsync(
      string id,
      Dictionary<string, object> updates,
      CancellationToken cancellationToken = default) {

    var existingRow = await _context.Set<PerspectiveRow<TModel>>()
        .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    if (existingRow == null) {
      throw new InvalidOperationException($"Cannot update fields for non-existent perspective row with ID '{id}'");
    }

    // Build new model with updated fields
    var updatedModel = UpdateModelFields(existingRow.Data, updates);

    // Update the row - remove and re-add to handle owned types properly
    _context.Set<PerspectiveRow<TModel>>().Remove(existingRow);

    var updatedRow = new PerspectiveRow<TModel> {
      Id = existingRow.Id,
      Data = updatedModel,
      Metadata = CloneMetadata(existingRow.Metadata),
      Scope = CloneScope(existingRow.Scope),
      CreatedAt = existingRow.CreatedAt,
      UpdatedAt = DateTime.UtcNow,
      Version = existingRow.Version + 1
    };

    _context.Set<PerspectiveRow<TModel>>().Add(updatedRow);

    await _context.SaveChangesAsync(cancellationToken);
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

  /// <summary>
  /// Creates a new instance of TModel with updated field values.
  /// Uses reflection to copy properties and apply updates.
  /// </summary>
  private static TModel UpdateModelFields(TModel original, Dictionary<string, object> updates) {
    var modelType = typeof(TModel);
    var properties = modelType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

    // Build dictionary of property values
    var propertyValues = new Dictionary<string, object?>();

    foreach (var prop in properties) {
      if (updates.ContainsKey(prop.Name)) {
        // Use updated value
        propertyValues[prop.Name] = updates[prop.Name];
      } else {
        // Preserve original value
        var value = prop.GetValue(original);
        propertyValues[prop.Name] = value;
      }
    }

    // Create new instance using parameterless constructor if available,
    // otherwise try to use a constructor with parameters
    var instance = Activator.CreateInstance(modelType);
    if (instance == null) {
      throw new InvalidOperationException($"Unable to create instance of {modelType.Name}");
    }

    // Set properties
    foreach (var kvp in propertyValues) {
      var prop = modelType.GetProperty(kvp.Key);
      if (prop != null && prop.CanWrite) {
        prop.SetValue(instance, kvp.Value);
      } else if (prop != null && prop.SetMethod != null) {
        // Handle init-only properties via reflection
        prop.SetMethod.Invoke(instance, new[] { kvp.Value });
      }
    }

    return (TModel)instance;
  }
}
