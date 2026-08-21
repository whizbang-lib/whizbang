using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Compile-time receptor-registry lookup the receive boundary uses to gate lifecycle
/// dispatch. Default implementation delegates to the source-generated
/// <c>Whizbang.Core.Generated.WhizbangReceptorRegistryQuery</c> static class — kept as
/// an interface seam so tests can inject deterministic answers without depending on
/// what receptors the test assembly happens to discover at compile time.
/// </summary>
/// <remarks>
/// Slice 4 of plans/pump-then-process.md (Half A — receive boundary). Used by
/// <c>InboxDispatchWorker</c> to skip the lifecycle deserialize for messages whose
/// stage has no registered receptor on this service. Avoids deserializing payloads
/// the service has no interest in — matters most for cross-service event types in BFF
/// patterns where many inbox events flow through with no local handler.
/// </remarks>
/// <docs>internals/receptor-registry-query</docs>
public interface IReceptorRegistryQuery {
  /// <summary>True when at least one receptor is registered for the given lifecycle
  /// stage + message type.</summary>
  bool HasReceptors(LifecycleStage stage, string messageType);

  /// <summary>True when an inbox handler (an <c>IReceptor</c> without a
  /// <c>FireAt</c> attribute) is registered for the message type.</summary>
  bool HasInboxHandler(string messageType);

  /// <summary>True when ANY consumer (handler / lifecycle receptor / perspective /
  /// tag-attribute) is registered for the message type.</summary>
  bool HasAnyConsumer(string messageType);

  /// <summary>
  /// Enumerates every receptor-handled message type known at compile time —
  /// (type name, contract namespace, kind) — deduplicated by type name, in a
  /// deterministic (ordinal type-name) order.
  /// </summary>
  /// <remarks>
  /// Default interface member returning an empty list: the predicates above were the
  /// interface's whole surface before the topology arc, so custom/test implementations
  /// in the wild only implement those. Defaulting the enumeration keeps every existing
  /// implementation compiling, and an empty answer degrades safely — topology consumers
  /// (subscription context, manifest) simply see no handled-message metadata and fall
  /// back to the singular strategy behavior.
  /// </remarks>
  /// <returns>The handled-message enumeration, or an empty list for implementations
  /// that predate the enumeration surface.</returns>
  /// <docs>internals/receptor-registry-query</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WhizbangReceptorRegistryQueryAdapterTests.cs:CustomPredicatesOnlyImplementation_GetHandledMessages_DefaultsToEmptyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WhizbangReceptorRegistryQueryAdapterTests.cs:GetHandledMessages_DelegatesToGeneratedStaticAsync</tests>
  IReadOnlyList<HandledMessageInfo> GetHandledMessages() => [];
}
