using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Static registry mapping model types to physical field hydrator delegates.
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
/// <para>Thread-safe for concurrent registration and lookup.</para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public static class PhysicalFieldHydratorRegistry {
  private static readonly ConcurrentDictionary<Type, Action<MaterializationInterceptionData, object>> _hydrators = new();

  /// <summary>
  /// Registers a physical field hydrator for a model type.
  /// Called by generated code at startup.
  /// </summary>
  /// <typeparam name="TModel">The perspective model type.</typeparam>
  /// <param name="hydrator">
  /// Delegate that reads shadow property values from <see cref="MaterializationInterceptionData"/>
  /// and sets them on the <c>Data</c> property of the <c>PerspectiveRow&lt;TModel&gt;</c>.
  /// </param>
  public static void Register<TModel>(Action<MaterializationInterceptionData, object> hydrator) where TModel : class {
    _hydrators[typeof(TModel)] = hydrator ?? throw new ArgumentNullException(nameof(hydrator));
  }

  /// <summary>
  /// Registers a physical field hydrator for a model type (non-generic).
  /// </summary>
  public static void Register(Type modelType, Action<MaterializationInterceptionData, object> hydrator) {
    _hydrators[modelType] = hydrator ?? throw new ArgumentNullException(nameof(hydrator));
  }

  /// <summary>
  /// Tries to get the hydrator for a model type.
  /// Returns false if no physical fields are registered for this type.
  /// </summary>
  public static bool TryGetHydrator(Type modelType, out Action<MaterializationInterceptionData, object> hydrator) {
    return _hydrators.TryGetValue(modelType, out hydrator!);
  }

  /// <summary>
  /// Clears all registered hydrators. Used for testing.
  /// </summary>
  internal static void Clear() => _hydrators.Clear();
}
