using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Static registry mapping perspective row types to physical field hydrator delegates.
/// Generated code registers hydrators at startup; the
/// <see cref="PhysicalFieldMaterializationInterceptor"/> uses them at query time.
/// </summary>
/// <remarks>
/// <para>
/// Each hydrator is a delegate that reads shadow property values from
/// <see cref="MaterializationInterceptionData.GetPropertyValue{T}(string)"/>
/// and copies them into the <c>Data</c> model instance. Hydrators are generated
/// at compile time (AOT-safe, no reflection).
/// </para>
/// <para>
/// Keyed by the closed generic type <c>typeof(PerspectiveRow&lt;TModel&gt;)</c> for
/// zero-reflection lookup at runtime.
/// </para>
/// <para>Thread-safe for concurrent registration and lookup.</para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public static class PhysicalFieldHydratorRegistry {
  private static readonly ConcurrentDictionary<Type, Action<MaterializationInterceptionData, object>> _hydrators = new();

  /// <summary>
  /// Registers a physical field hydrator for a perspective row type.
  /// Called by generated code at startup.
  /// </summary>
  /// <param name="perspectiveRowType">
  /// The closed generic type <c>typeof(PerspectiveRow&lt;TModel&gt;)</c>.
  /// </param>
  /// <param name="hydrator">
  /// Delegate that reads shadow property values from <see cref="MaterializationInterceptionData"/>
  /// and sets them on the <c>Data</c> property of the <c>PerspectiveRow&lt;TModel&gt;</c>.
  /// </param>
  public static void Register(Type perspectiveRowType, Action<MaterializationInterceptionData, object> hydrator) {
    ArgumentNullException.ThrowIfNull(perspectiveRowType);
    _hydrators[perspectiveRowType] = hydrator ?? throw new ArgumentNullException(nameof(hydrator));
  }

  /// <summary>
  /// Registers a physical field hydrator keyed by model type (legacy overload).
  /// Wraps the key as <c>typeof(PerspectiveRow&lt;TModel&gt;)</c> internally.
  /// </summary>
  /// <typeparam name="TModel">The perspective model type.</typeparam>
  /// <param name="hydrator">
  /// Delegate that reads shadow property values from <see cref="MaterializationInterceptionData"/>
  /// and sets them on the <c>Data</c> property of the <c>PerspectiveRow&lt;TModel&gt;</c>.
  /// </param>
  public static void Register<TModel>(Action<MaterializationInterceptionData, object> hydrator) where TModel : class {
    _hydrators[typeof(Whizbang.Core.Lenses.PerspectiveRow<TModel>)] = hydrator ?? throw new ArgumentNullException(nameof(hydrator));
  }

  /// <summary>
  /// Tries to get the hydrator for an entity type.
  /// Returns false if no physical fields are registered for this type.
  /// </summary>
  /// <param name="entityType">The runtime type of the entity (e.g., <c>typeof(PerspectiveRow&lt;TModel&gt;)</c>).</param>
  /// <param name="hydrator">The hydrator delegate if found.</param>
  public static bool TryGetHydrator(Type entityType, out Action<MaterializationInterceptionData, object> hydrator) {
    return _hydrators.TryGetValue(entityType, out hydrator!);
  }

  /// <summary>
  /// Clears all registered hydrators. Used for testing.
  /// </summary>
  internal static void Clear() => _hydrators.Clear();
}
