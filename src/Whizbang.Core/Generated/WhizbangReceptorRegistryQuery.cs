// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  // Cached aggregated views live in ONE immutable snapshot published through a single
  // reference. AssemblyRegistry<T> doesn't notify on register, so the snapshot records the
  // contribution count it was built from; when the registry's count differs, the next query
  // builds a fresh snapshot and publishes it.
  //
  // No lock: the build is a pure function of the registry, so two threads that race each
  // build an equal snapshot and the last write wins harmlessly. Publishing every view through
  // one reference also means a reader can never observe views from two different builds.
  private static Snapshot? _snapshot;

  /// <summary>True if any receptor is registered for the given lifecycle stage + message type.</summary>
  public static bool HasReceptors(LifecycleStage stage, string messageType) {
    var stageTypes = _currentSnapshot().StageTypes;
    return stageTypes.TryGetValue(stage, out var set) && set.Contains(_normalizeTypeName(messageType));
  }

  /// <summary>True if any inbox handler (IReceptor without lifecycle FireAt) is registered for the type.</summary>
  public static bool HasInboxHandler(string messageType) {
    return _currentSnapshot().InboxHandler.Contains(_normalizeTypeName(messageType));
  }

  /// <summary>True if any consumer (handler / lifecycle receptor / perspective / tag-attribute) cares about this message type.</summary>
  public static bool HasAnyConsumer(string messageType) {
    return _currentSnapshot().AnyConsumer.Contains(_normalizeTypeName(messageType));
  }

  /// <summary>
  /// Enumerates every receptor-handled message type across all loaded assemblies'
  /// contributions — deduplicated by message type name (first contribution wins) and
  /// sorted ordinally by type name so topology projections (subscription context,
  /// manifest, drift checks) see a deterministic sequence.
  /// </summary>
  /// <returns>The merged handled-message enumeration; empty when no contribution
  /// carries handled-message metadata.</returns>
  /// <docs>internals/receptor-registry-query</docs>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangReceptorRegistryQueryAggregationTests.cs:TwoContributions_GetHandledMessages_UnionsAndDeduplicatesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangReceptorRegistryQueryAggregationTests.cs:GetHandledMessages_DeterministicOrder_SortedByTypeNameAsync</tests>
  public static IReadOnlyList<HandledMessageInfo> GetHandledMessages() {
    return _currentSnapshot().HandledMessages;
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

  // ===== Snapshot (re)building =====

  /// <summary>
  /// Returns the snapshot for the registry's current contribution count: the published one
  /// when it is current, otherwise a freshly built one that is then published.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangReceptorRegistryQuerySnapshotTests.cs:FirstQuery_OnAnEmptyCache_BuildsTheSnapshotAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangReceptorRegistryQuerySnapshotTests.cs:RepeatedQuery_WithUnchangedCount_ReusesTheSnapshotAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangReceptorRegistryQuerySnapshotTests.cs:ContributionRegisteredAfterFirstQuery_RebuildsTheSnapshotAsync</tests>
  private static Snapshot _currentSnapshot() {
    var currentCount = AssemblyRegistry<ReceptorRegistryContribution>.Count;
    var snapshot = Volatile.Read(ref _snapshot);
    if (snapshot is not null && snapshot.ContributionCount == currentCount) {
      return snapshot;
    }
    snapshot = _buildSnapshot(currentCount);
    Volatile.Write(ref _snapshot, snapshot);
    return snapshot;
  }

  private static Snapshot _buildSnapshot(int contributionCount) {
    var any = new HashSet<string>(System.StringComparer.Ordinal);
    var inbox = new HashSet<string>(System.StringComparer.Ordinal);
    var stages = new Dictionary<LifecycleStage, HashSet<string>>();
    var handledByName = new Dictionary<string, HandledMessageInfo>(System.StringComparer.Ordinal);
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
      foreach (var handled in contribution.HandledMessages) {
        // Dedupe by type name across assemblies (first contribution wins — the same type
        // handled in two assemblies carries identical namespace/kind metadata anyway).
        handledByName.TryAdd(handled.MessageTypeName, handled);
      }
    }
    var handledList = new List<HandledMessageInfo>(handledByName.Values);
    handledList.Sort(static (a, b) => string.CompareOrdinal(a.MessageTypeName, b.MessageTypeName));
    return new Snapshot(contributionCount, any, inbox, stages, handledList);
  }

  /// <summary>
  /// Test-only: drops the published snapshot so the next query rebuilds from the
  /// current <see cref="AssemblyRegistry{T}"/> state. Use this AFTER calling
  /// <c>AssemblyRegistry&lt;ReceptorRegistryContribution&gt;.ClearForTesting()</c> to
  /// keep test isolation tight.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangReceptorRegistryQuerySnapshotTests.cs:ClearCacheForTesting_DropsThePublishedSnapshotAsync</tests>
  internal static void ClearCacheForTesting() {
    Volatile.Write(ref _snapshot, null);
  }

  /// <summary>
  /// Diagnostic snapshot — for operator inspection that the multi-assembly registry
  /// pattern populated correctly. The receive-boundary drop-gate silently breaks if
  /// this returns zero in production: nothing is registered, every message gets dropped.
  /// </summary>
  /// <returns>Tuple of (contribution count, distinct any-consumer types, distinct inbox-handler types,
  /// distinct lifecycle-stage receptor types across all stages).</returns>
  public static (int Contributions, int AnyConsumerTypes, int InboxHandlerTypes, int StageTypeCount) GetDiagnosticSnapshot() {
    var snapshot = _currentSnapshot();
    var stageCount = 0;
    foreach (var (_, set) in snapshot.StageTypes) {
      stageCount += set.Count;
    }
    return (
      Contributions: AssemblyRegistry<ReceptorRegistryContribution>.Count,
      AnyConsumerTypes: snapshot.AnyConsumer.Count,
      InboxHandlerTypes: snapshot.InboxHandler.Count,
      StageTypeCount: stageCount);
  }

  /// <summary>
  /// One immutable build of every aggregated view, tagged with the contribution count it
  /// was built from. Never mutated after construction, so it is safe to share across threads.
  /// </summary>
  private sealed class Snapshot(
      int contributionCount,
      HashSet<string> anyConsumer,
      HashSet<string> inboxHandler,
      Dictionary<LifecycleStage, HashSet<string>> stageTypes,
      IReadOnlyList<HandledMessageInfo> handledMessages) {
    public int ContributionCount { get; } = contributionCount;
    public HashSet<string> AnyConsumer { get; } = anyConsumer;
    public HashSet<string> InboxHandler { get; } = inboxHandler;
    public Dictionary<LifecycleStage, HashSet<string>> StageTypes { get; } = stageTypes;
    public IReadOnlyList<HandledMessageInfo> HandledMessages { get; } = handledMessages;
  }
}
