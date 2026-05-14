using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Slice 22a (plans/slice-22-source-gen-atomic-upsert.md) — runtime registry that maps a
/// perspective <c>TModel</c> type to a source-generated atomic-upsert strategy.
/// </summary>
/// <remarks>
/// <para>
/// Populated at process start by per-consumer <c>[ModuleInitializer]</c> code emitted by
/// <c>PerspectiveAtomicUpsertGenerator</c> (slice 22c). Looked up at call time by
/// <c>BaseUpsertStrategy._upsertCoreInnerAsync</c> (slice 22d) — when a strategy is
/// registered for the row's <c>TModel</c> AND there are no physical-field overrides, the
/// fast path runs; otherwise the legacy SELECT-then-INSERT/UPDATE flow takes over and
/// slice 19's retry catches any 23505.
/// </para>
/// <para>
/// Concurrency model: <see cref="ConcurrentDictionary{TKey,TValue}"/> backed, lock-free
/// lookup. Registration is idempotent — last-write-wins. The registry never throws on
/// re-registration so the generator can emit the registration unconditionally in each
/// consumer assembly's module init without needing a dedup ceremony.
/// </para>
/// </remarks>
/// <docs>extending/internals/event-ordering-invariant</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveAtomicUpsertRegistryTests.cs</tests>
public static class PerspectiveAtomicUpsertRegistry {
  private static readonly ConcurrentDictionary<Type, IPerspectiveAtomicUpsertStrategy> _registry = new();

  /// <summary>
  /// Register a typed atomic-upsert strategy for a perspective <c>TModel</c>. Called by
  /// generator-emitted module init code; subsequent registrations for the same type
  /// replace the prior entry (last-write-wins).
  /// </summary>
  public static void Register(Type modelType, IPerspectiveAtomicUpsertStrategy strategy) {
    ArgumentNullException.ThrowIfNull(modelType);
    ArgumentNullException.ThrowIfNull(strategy);
    _registry[modelType] = strategy;
  }

  /// <summary>
  /// Look up the registered strategy for a perspective <c>TModel</c>. Returns <c>false</c>
  /// when no generator-emitted strategy exists (caller falls back to the legacy path).
  /// </summary>
  public static bool TryGet(Type modelType, [NotNullWhen(true)] out IPerspectiveAtomicUpsertStrategy? strategy) {
    if (modelType is null) {
      strategy = null;
      return false;
    }
    return _registry.TryGetValue(modelType, out strategy);
  }

  /// <summary>
  /// Total number of registered TModel strategies. Exposed for diagnostics only.
  /// </summary>
  public static int Count => _registry.Count;
}
