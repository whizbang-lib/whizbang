using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default <see cref="IReceptorRegistryQuery"/> — delegates to the source-generated
/// <c>Whizbang.Core.Generated.WhizbangReceptorRegistryQuery</c> static class. AOT-safe;
/// no reflection.
/// </summary>
/// <remarks>Registered as a singleton by
/// <c>WorkerPipelineExtensions.AddWhizbangWorkers</c>.</remarks>
/// <docs>internals/receptor-registry-query</docs>
/// <param name="runtimeRegistry">The runtime receptor registry; when supplied, runtime-registered
/// receptors (the control-plane surface) count as consumers at the discard gates.</param>
public sealed class WhizbangReceptorRegistryQueryAdapter(IReceptorRegistry? runtimeRegistry = null) : IReceptorRegistryQuery {
  /// <inheritdoc />
  public bool HasReceptors(LifecycleStage stage, string messageType)
    => Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.HasReceptors(stage, messageType);

  /// <inheritdoc />
  public bool HasInboxHandler(string messageType)
    => Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.HasInboxHandler(messageType);

  /// <inheritdoc />
  public bool HasAnyConsumer(string messageType)
    => Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.HasAnyConsumer(messageType)
       || runtimeRegistry?.HasRuntimeConsumerFor(messageType) == true;

  /// <inheritdoc />
  /// <remarks>Runtime-registered receptors (the control-plane surface) are NOT enumerated
  /// here — they register after startup and carry no compile-time namespace/kind metadata;
  /// the predicate surface (<see cref="HasAnyConsumer"/>) still covers them at the discard
  /// gates.</remarks>
  public IReadOnlyList<HandledMessageInfo> GetHandledMessages()
    => Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.GetHandledMessages();
}
