using Microsoft.EntityFrameworkCore.Diagnostics;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Materialization interceptor that hydrates physical field values from shadow properties
/// into the <c>Data</c> model after EF Core materializes a <see cref="PerspectiveRow{TModel}"/>.
/// </summary>
/// <remarks>
/// <para>
/// In Split mode, physical fields are stored only in physical database columns (not in JSONB).
/// EF Core's <c>ComplexProperty().ToJson()</c> materializes <c>Data</c> exclusively from JSONB,
/// so physical fields come back as null/default. This interceptor fixes that by copying
/// shadow property values into the model after materialization.
/// </para>
/// <para>
/// Uses <see cref="MaterializationInterceptionData.GetPropertyValue{T}(string)"/> which reads
/// from the materialization value buffer (not the change tracker), so it works with
/// <c>AsNoTracking()</c> queries.
/// </para>
/// <para>
/// Hydrators are registered via <see cref="PhysicalFieldHydratorRegistry"/> by generated code
/// at startup. Each hydrator is a delegate that copies shadow property values into the model
/// using compile-time generated setters (AOT-safe, no reflection).
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public class PhysicalFieldMaterializationInterceptor : IMaterializationInterceptor {
  /// <inheritdoc/>
  public object InitializedInstance(
    MaterializationInterceptionData materializationData,
    object entity) {

    // Only process PerspectiveRow<TModel> entities
    var entityType = entity.GetType();
    if (!entityType.IsGenericType || entityType.GetGenericTypeDefinition() != typeof(PerspectiveRow<>)) {
      return entity;
    }

    // Get the model type (TModel from PerspectiveRow<TModel>)
    var modelType = entityType.GetGenericArguments()[0];

    // Look up generated hydrator for this model type
    if (!PhysicalFieldHydratorRegistry.TryGetHydrator(modelType, out var hydrator)) {
      return entity; // No physical fields registered for this model
    }

    // Hydrate: copy physical column values from shadow properties into Data
    hydrator(materializationData, entity);
    return entity;
  }
}
