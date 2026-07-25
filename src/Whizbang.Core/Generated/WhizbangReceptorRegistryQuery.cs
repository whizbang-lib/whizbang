using System.Collections.Generic;
using System.Threading;
using Whizbang.Core.Messaging;
using Whizbang.Core.Registry;

namespace Whizbang.Core.Generated;

/// <summary>
/// Aggregating compile-time-known consumer registry. Reads contributions from
/// <see cref="AssemblyRegistry{T}"/> of <see cref="ReceptorRegistryContribution"/>,
/// which each consuming assembly populates at load time via a generated
/// <c>[ModuleInitializer]</c>. The receive-boundary drop-gate and lifecycle gates query
/// this static for "is anyone consuming this type" decisions across the union of all
/// loaded assemblies.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this design:</strong> the original slice 1 generator emitted a duplicate
/// <c>WhizbangReceptorRegistryQuery</c> static class with HashSets baked in into
/// <em>every</em> consuming assembly. The
/// <see cref="WhizbangReceptorRegistryQueryAdapter"/> in Whizbang.Core binds at compile
/// time to Whizbang.Core's in-Core copy — which is always EMPTY because Whizbang.Core
/// has no receptors. In production runs of consumer services, the adapter therefore
/// returned false for every <c>HasAnyConsumer</c> / <c>HasReceptors</c> query, and the
/// receive-boundary drop-gate silently dropped every message. Symptom: chat would not
/// load for a consumer application because no User / Permissions events ever flowed. The fix: use
/// <see cref="AssemblyRegistry{T}"/> — Whizbang's existing reusable primitive for
/// multi-assembly contribution registration via <c>[ModuleInitializer]</c>. Same pattern
/// as <c>AutoPopulatePopulatorRegistry</c>, <c>JsonContextRegistry</c>,
/// <c>WhizbangIdProviderRegistry</c>.
/// </para>
/// <para>Slice 1 of plans/pump-then-process.md (revised 2026-05-06).</para>
/// </remarks>
/// <docs>internals/receptor-registry-query</docs>
public static class WhizbangReceptorRegistryQuery {
  // Cached aggregated views, invalidated on Register-via-AssemblyRegistry.
  // Note: AssemblyRegistry<T> doesn't notify on register, so we cache by contribution
  // count — when count changes we rebuild. Simple, allocation-light, and correct for
  // the load-time-then-query-many access pattern.
  private static readonly Lock _lock = new();
  private static int _cachedContributionCount = -1;
  private static HashSet<string>? _cachedAnyConsumer;
  private static HashSet<string>? _cachedInboxHandler;
  private static Dictionary<LifecycleStage, HashSet<string>>? _cachedStageTypes;

  /// <summary>True if any receptor is registered for the given lifecycle stage + message type.</summary>
  public static bool HasReceptors(LifecycleStage stage, string messageType) {
    var stageTypes = _ensureStageTypesCached();
    return stageTypes.TryGetValue(stage, out var set) && set.Contains(_normalizeTypeName(messageType));
  }

  /// <summary>True if any inbox handler (IReceptor without lifecycle FireAt) is registered for the type.</summary>
  public static bool HasInboxHandler(string messageType) {
    var inboxHandlers = _ensureInboxHandlerCached();
    return inboxHandlers.Contains(_normalizeTypeName(messageType));
  }

  /// <summary>True if any consumer (handler / lifecycle receptor / perspective / tag-attribute) cares about this message type.</summary>
  public static bool HasAnyConsumer(string messageType) {
    var anyConsumer = _ensureAnyConsumerCached();
    return anyConsumer.Contains(_normalizeTypeName(messageType));
  }

  /// <summary>
  /// Normalizes a CLR type name to match the C# display format used by the source generator.
  /// The generator uses <c>SymbolDisplayFormat.FullyQualifiedFormat</c> which produces dots for
  /// nested types (e.g., <c>"Ns.Outer.Inner"</c>), but at runtime <c>Type.AssemblyQualifiedName</c>
  /// uses <c>+</c> for nested types and includes the assembly (e.g., <c>"Ns.Outer+Inner, Asm"</c>).
  /// This method bridges the two formats so the drop-gate lookup works correctly.
  /// </summary>
  private static string _normalizeTypeName(string typeName) {
    // Strip assembly qualifier (everything after first ", ")
    var commaIdx = typeName.IndexOf(", ", System.StringComparison.Ordinal);
    var nameOnly = commaIdx >= 0 ? typeName[..commaIdx] : typeName;
    // Convert CLR nested-type separator (+) to C# display format (.)
    return nameOnly.Contains('+') ? nameOnly.Replace('+', '.') : nameOnly;
  }

  // ===== Cache (re)building =====

  private static HashSet<string> _ensureAnyConsumerCached() {
    var current = AssemblyRegistry<ReceptorRegistryContribution>.Count;
    if (_cachedAnyConsumer is not null && _cachedContributionCount == current) {
      return _cachedAnyConsumer;
    }
    lock (_lock) {
      _rebuildIfStale(current);
      return _cachedAnyConsumer!;
    }
  }

  private static HashSet<string> _ensureInboxHandlerCached() {
    var current = AssemblyRegistry<ReceptorRegistryContribution>.Count;
    if (_cachedInboxHandler is not null && _cachedContributionCount == current) {
      return _cachedInboxHandler;
    }
    lock (_lock) {
      _rebuildIfStale(current);
      return _cachedInboxHandler!;
    }
  }

  private static Dictionary<LifecycleStage, HashSet<string>> _ensureStageTypesCached() {
    var current = AssemblyRegistry<ReceptorRegistryContribution>.Count;
    if (_cachedStageTypes is not null && _cachedContributionCount == current) {
      return _cachedStageTypes;
    }
    lock (_lock) {
      _rebuildIfStale(current);
      return _cachedStageTypes!;
    }
  }

  private static void _rebuildIfStale(int currentCount) {
    if (_cachedAnyConsumer is not null && _cachedContributionCount == currentCount) {
      return;
    }
    var any = new HashSet<string>(System.StringComparer.Ordinal);
    var inbox = new HashSet<string>(System.StringComparer.Ordinal);
    var stages = new Dictionary<LifecycleStage, HashSet<string>>();
    foreach (var contribution in AssemblyRegistry<ReceptorRegistryContribution>.GetOrderedContributions()) {
      foreach (var t in contribution.AnyConsumerTypes) {
        any.Add(t);
      }
      foreach (var t in contribution.InboxHandlerTypes) {
        inbox.Add(t);
      }
      foreach (var (stage, types) in contribution.StageTypes) {
        if (!stages.TryGetValue(stage, out var set)) {
          set = new HashSet<string>(System.StringComparer.Ordinal);
          stages[stage] = set;
        }
        foreach (var t in types) {
          set.Add(t);
        }
      }
    }
    _cachedAnyConsumer = any;
    _cachedInboxHandler = inbox;
    _cachedStageTypes = stages;
    _cachedContributionCount = currentCount;
  }

  /// <summary>
  /// Test-only: invalidates the local caches so the next query rebuilds from the
  /// current <see cref="AssemblyRegistry{T}"/> state. Use this AFTER calling
  /// <c>AssemblyRegistry&lt;ReceptorRegistryContribution&gt;.ClearForTesting()</c> to
  /// keep test isolation tight.
  /// </summary>
  internal static void ClearCacheForTesting() {
    lock (_lock) {
      _cachedAnyConsumer = null;
      _cachedInboxHandler = null;
      _cachedStageTypes = null;
      _cachedContributionCount = -1;
    }
  }

  /// <summary>
  /// Diagnostic snapshot — for operator inspection that the multi-assembly registry
  /// pattern populated correctly. The receive-boundary drop-gate silently breaks if
  /// this returns zero in production: nothing is registered, every message gets dropped.
  /// </summary>
  /// <returns>Tuple of (contribution count, distinct any-consumer types, distinct inbox-handler types,
  /// distinct lifecycle-stage receptor types across all stages).</returns>
  public static (int Contributions, int AnyConsumerTypes, int InboxHandlerTypes, int StageTypeCount) GetDiagnosticSnapshot() {
    var any = _ensureAnyConsumerCached();
    var inbox = _ensureInboxHandlerCached();
    var stages = _ensureStageTypesCached();
    var stageCount = 0;
    foreach (var (_, set) in stages) {
      stageCount += set.Count;
    }
    return (
      Contributions: AssemblyRegistry<ReceptorRegistryContribution>.Count,
      AnyConsumerTypes: any.Count,
      InboxHandlerTypes: inbox.Count,
      StageTypeCount: stageCount);
  }
}
