using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Runtime map of a <see cref="FieldStorageMode.Split"/> model to its <see cref="SplitPhysicalFieldMap{TModel}"/>,
/// so a store can load the promoted fields the document never holds.
/// </summary>
/// <remarks>
/// <para>
/// Populated at startup by the <c>[ModuleInitializer]</c> the perspective runner generator emits for a Split
/// model. A store consults it when it reads a model: without it the model a later event is applied to would
/// carry the defaults of every promoted field, and the write that follows would store those defaults over
/// the columns.
/// </para>
/// <para>A model with no registration is not stored Split, and its store reads the document alone.</para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/SplitPhysicalFieldRegistryTests.cs</tests>
public static class SplitPhysicalFieldRegistry {
  private static readonly ConcurrentDictionary<Type, object> _maps = new();

  /// <summary>Registers a Split model's promoted columns. Idempotent: the last registration wins.</summary>
  /// <typeparam name="TModel">The perspective's read model.</typeparam>
  /// <param name="map">Its promoted columns and the code that copies them into a loaded model.</param>
  public static void Register<TModel>(SplitPhysicalFieldMap<TModel> map) where TModel : class {
    ArgumentNullException.ThrowIfNull(map);
    _maps[typeof(TModel)] = map;
  }

  /// <summary>The model's promoted columns, when it is stored Split.</summary>
  /// <typeparam name="TModel">The perspective's read model.</typeparam>
  /// <param name="map">The registered map, or null when the model has none.</param>
  /// <returns>Whether the model is registered.</returns>
  public static bool TryGet<TModel>([global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SplitPhysicalFieldMap<TModel>? map)
      where TModel : class {
    map = _maps.TryGetValue(typeof(TModel), out var registered) ? (SplitPhysicalFieldMap<TModel>)registered : null;
    return map is not null;
  }
}
