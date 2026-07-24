using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives.Hooks;

/// <summary>
/// A producer that, given a per-apply <see cref="ApplyHookContext"/>, runs one registered hook and returns the
/// declarative op list it recorded. The registry hands the executor a list of these (one per matching hook, in
/// registration order); the executor invokes each with the context and interprets the combined ops.
/// </summary>
/// <param name="context">The per-apply context.</param>
/// <returns>The ops the hook recorded for this apply.</returns>
public delegate IReadOnlyList<ApplyHookOp> ApplyHookProducer(ApplyHookContext context);

/// <summary>
/// Shared marker-matching store for apply-hook registrations: keyed-override, registration-order preservation,
/// and a per-model memoized resolve. A hook is registered against a marker type; it matches a model
/// <c>TModel</c> when <c>marker.IsAssignableFrom(TModel)</c>. This base holds the type-erased mechanics; the
/// two concrete registries (<see cref="CollectiveApplyHookRegistry"/>, <see cref="ApplyHookRegistry"/>) add the
/// strongly-typed <c>Register</c> entry points that build the producers.
/// </summary>
/// <docs>fundamentals/messaging/apply-hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Hooks/ApplyHookRegistryTests.cs:BaseClassMarker_FiresForDerivedModelAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Hooks/ApplyHookRegistryTests.cs:KeyedOverride_ReplacesInPlace_KeepingOrderPositionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Hooks/ApplyHookRegistryTests.cs:DifferentMarkers_MatchingModel_FireInRegistrationOrderAsync</tests>
public abstract class MarkerHookRegistryBase {
  private readonly List<_registration> _registrations = [];
  private readonly Dictionary<string, int> _keyIndex = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<Type, IReadOnlyList<ApplyHookProducer>> _cache = new();
  private readonly Lock _gate = new();

  private sealed record _registration(Type Marker, string? Key, ApplyHookProducer Producer);

  /// <summary>
  /// Add a producer under <paramref name="marker"/>. When <paramref name="key"/> is non-null and already
  /// registered, the existing entry is REPLACED in place (its order position preserved); otherwise the entry is
  /// appended. Unkeyed registrations always append.
  /// </summary>
  /// <param name="marker">The marker type the hook is gated on.</param>
  /// <param name="key">Optional override key (see <see cref="WhizbangApplyHookKeys"/>).</param>
  /// <param name="producer">The type-erased op producer for the hook.</param>
  protected void Add(Type marker, string? key, ApplyHookProducer producer) {
    ArgumentNullException.ThrowIfNull(marker);
    ArgumentNullException.ThrowIfNull(producer);
    var entry = new _registration(marker, key, producer);
    lock (_gate) {
      if (key is not null && _keyIndex.TryGetValue(key, out var idx)) {
        // Override in place: replace the hook at the existing key's slot, preserving its order position.
        _registrations[idx] = entry;
      } else {
        _registrations.Add(entry);
        if (key is not null) {
          _keyIndex[key] = _registrations.Count - 1;
        }
      }
      // Registrations changed — drop the per-model memoization so a later resolve re-scans.
      _cache.Clear();
    }
  }

  /// <summary>
  /// Resolve the ordered list of producers whose marker matches <paramref name="modelType"/>
  /// (<c>marker.IsAssignableFrom(modelType)</c>), in registration order. Memoized per model type — the
  /// <c>IsAssignableFrom</c> scan runs once per model, never on the apply hot path.
  /// </summary>
  /// <param name="modelType">The perspective model type being applied.</param>
  /// <returns>The matching producers, in registration order (empty when none match).</returns>
  public IReadOnlyList<ApplyHookProducer> ResolveFor(Type modelType) {
    ArgumentNullException.ThrowIfNull(modelType);
    return _cache.GetOrAdd(modelType, static (mt, self) => {
      // The one-time IsAssignableFrom scan for this model, memoized. Snapshot under the gate so a concurrent
      // Add can't tear the list mid-scan. Registration order is the list order.
      lock (self._gate) {
        return self._registrations
          .Where(r => r.Marker.IsAssignableFrom(mt))
          .Select(r => r.Producer)
          .ToArray();
      }
    }, this);
  }
}

/// <summary>
/// Registry of <see cref="ICollectiveApplyHook{TMarker}"/> hooks for the collective (set-based UPDATE) path.
/// </summary>
/// <docs>fundamentals/messaging/apply-hooks</docs>
public sealed class CollectiveApplyHookRegistry : MarkerHookRegistryBase {
  /// <summary>
  /// Register a collective hook against marker <typeparamref name="TMarker"/>. Pass a <paramref name="key"/> to
  /// make it overridable in place (re-registering the same key replaces it).
  /// </summary>
  /// <typeparam name="TMarker">The marker the hook is gated on.</typeparam>
  /// <param name="hook">The hook to register.</param>
  /// <param name="key">Optional override key.</param>
  /// <returns>This registry, for chaining.</returns>
  public CollectiveApplyHookRegistry Register<TMarker>(ICollectiveApplyHook<TMarker> hook, string? key = null) {
    ArgumentNullException.ThrowIfNull(hook);
    Add(typeof(TMarker), key, context => {
      var builder = new CollectiveApplyHookBuilder<TMarker>();
      hook.Configure(builder, context);
      return builder.Ops;
    });
    return this;
  }
}

/// <summary>
/// Registry of <see cref="IApplyHook{TMarker}"/> hooks for the per-event (loaded-row object mutation) path.
/// </summary>
/// <docs>fundamentals/messaging/apply-hooks</docs>
public sealed class ApplyHookRegistry : MarkerHookRegistryBase {
  /// <summary>
  /// Register a per-event hook against marker <typeparamref name="TMarker"/>. Pass a <paramref name="key"/> to
  /// make it overridable in place (re-registering the same key replaces it).
  /// </summary>
  /// <typeparam name="TMarker">The marker the hook is gated on.</typeparam>
  /// <param name="hook">The hook to register.</param>
  /// <param name="key">Optional override key.</param>
  /// <returns>This registry, for chaining.</returns>
  public ApplyHookRegistry Register<TMarker>(IApplyHook<TMarker> hook, string? key = null) {
    ArgumentNullException.ThrowIfNull(hook);
    Add(typeof(TMarker), key, context => {
      var builder = new ApplyHookBuilder<TMarker>();
      hook.Configure(builder, context);
      return builder.Ops;
    });
    return this;
  }
}
