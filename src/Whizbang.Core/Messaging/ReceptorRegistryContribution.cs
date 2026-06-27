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
  /// Concrete <c>ICompositeEvent</c> message types declared in this assembly. A composite travels over
  /// transport like an IEvent but is never event-stored; it auto-fans-out at every destination service,
  /// including its own (so the receive boundary must NOT echo-discard an owned composite — see
  /// <see cref="Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.IsComposite"/>). Defaulted to empty
  /// so a contribution emitted before this field existed merges harmlessly.
  /// </summary>
  public IReadOnlyCollection<string> CompositeTypes { get; init; } = System.Array.Empty<string>();
}
