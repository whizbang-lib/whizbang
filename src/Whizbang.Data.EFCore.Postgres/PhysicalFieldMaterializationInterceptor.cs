using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Materialization interceptor that hydrates physical field values from shadow properties
/// into the <c>Data</c> model after EF Core materializes a <c>PerspectiveRow{TModel}</c>.
/// </summary>
/// <remarks>
/// <para>
/// In Split mode, physical fields are stored only in physical database columns (not in JSONB).
/// EF Core's <c>ComplexProperty().ToJson()</c> materializes <c>Data</c> exclusively from JSONB,
/// so physical fields come back as null/default. This interceptor attempts to fix that by copying
/// shadow property values into the model after materialization.
/// </para>
/// <para>
/// <strong>Known limitation with ComplexProperty().ToJson():</strong>
/// <c>InitializedInstance</c> fires BEFORE <c>ComplexProperty().ToJson()</c> populates
/// the <c>Data</c> property. When <c>row.Data</c> is null, this interceptor safely no-ops.
/// The primary hydration path for production is <see cref="SplitModeChangeTrackerHydrator"/>
/// which uses the <c>ChangeTracker.Tracked</c> event (fires after full materialization).
/// This interceptor is kept as a fallback for scenarios where Data is populated before
/// the interceptor fires (potential future EF Core behavior change).
/// </para>
/// <para>
/// <strong>Zero reflection, AOT-safe:</strong> Uses <c>entity.GetType()</c> (CLR intrinsic)
/// + dictionary lookup. No <c>IsGenericType</c>, no <c>GetGenericTypeDefinition()</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public class PhysicalFieldMaterializationInterceptor : IMaterializationInterceptor {
  /// <inheritdoc/>
  public object InitializedInstance(
    MaterializationInterceptionData materializationData,
    object entity) {

    // Zero-reflection lookup: dictionary keyed by exact runtime type
    if (!PhysicalFieldHydratorRegistry.TryGetHydrator(entity.GetType(), out var hydrator)) {
      return entity;
    }

    // Hydrate: copy physical column values from shadow properties into Data
    hydrator(materializationData, entity);
    return entity;
  }
}
