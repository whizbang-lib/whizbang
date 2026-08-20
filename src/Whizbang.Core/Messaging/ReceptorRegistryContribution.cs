using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// One assembly's contribution to the receptor / lifecycle / consumer registry. Each
/// consuming assembly's source generator emits a <c>[ModuleInitializer]</c>-decorated
/// method that creates one of these and registers it with
/// <see cref="Registry.AssemblyRegistry{T}"/>. The aggregating
/// <see cref="Whizbang.Core.Generated.WhizbangReceptorRegistryQuery"/> static reads the
/// merged set of contributions across all loaded assemblies.
/// </summary>
/// <remarks>
/// Slice 1 of plans/pump-then-process.md (revised 2026-05-06 — see
/// <see cref="Whizbang.Core.Generated.WhizbangReceptorRegistryQuery"/> for the
/// production-bug history that motivated the AssemblyRegistry-based redesign).
/// </remarks>
/// <docs>internals/receptor-registry-query</docs>
public sealed class ReceptorRegistryContribution {
  /// <summary>Message types whose handler / lifecycle receptor / perspective / tag-attribute
  /// is registered in this assembly.</summary>
  public required IReadOnlyCollection<string> AnyConsumerTypes { get; init; }

  /// <summary>Message types with at least one direct inbox handler
  /// (<c>IReceptor&lt;T&gt;</c> without a <c>FireAt</c> attribute) in this assembly.</summary>
  public required IReadOnlyCollection<string> InboxHandlerTypes { get; init; }

  /// <summary>Per-stage receptor message types registered in this assembly. Keyed by
  /// the lifecycle stage; values are the message types with at least one
  /// <c>[FireAt(stage)]</c> receptor.</summary>
  public required IReadOnlyDictionary<LifecycleStage, IReadOnlyCollection<string>> StageTypes { get; init; }

  /// <summary>
  /// Enumeration of every receptor-handled message type in this assembly —
  /// (type name, contract namespace, kind) — for the routing seam (topology arc phase 3).
  /// NOT <c>required</c>: contributions built by older generator output or hand-built in
  /// tests keep compiling and simply contribute no handled-message metadata.
  /// </summary>
  public IReadOnlyList<HandledMessageInfo> HandledMessages { get; init; } = [];
}
