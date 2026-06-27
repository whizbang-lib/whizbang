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

  /// <summary>True when the message type is a concrete <c>ICompositeEvent</c>. The receive boundary
  /// uses this to exempt owned composites from echo-discard so they survive to the dispatch-time
  /// fan-out at every destination service, including the publishing service itself.</summary>
  /// <remarks>Default-implemented as <c>false</c> so existing test doubles need no change; the
  /// generated <see cref="WhizbangReceptorRegistryQueryAdapter"/> overrides it with the real lookup.</remarks>
  bool IsComposite(string messageType) => false;
}
