namespace Whizbang.Core.Messaging;

/// <summary>
/// Default <see cref="IReceptorRegistryQuery"/> — delegates to the source-generated
/// <c>Whizbang.Core.Generated.WhizbangReceptorRegistryQuery</c> static class. AOT-safe;
/// no reflection.
/// </summary>
/// <remarks>Registered as a singleton by
/// <c>WorkerPipelineExtensions.AddWhizbangWorkers</c>.</remarks>
/// <docs>internals/receptor-registry-query</docs>
public sealed class WhizbangReceptorRegistryQueryAdapter : IReceptorRegistryQuery {
  /// <inheritdoc />
  public bool HasReceptors(LifecycleStage stage, string messageType)
    => Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.HasReceptors(stage, messageType);

  /// <inheritdoc />
  public bool HasInboxHandler(string messageType)
    => Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.HasInboxHandler(messageType);

  /// <inheritdoc />
  public bool HasAnyConsumer(string messageType)
    => Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.HasAnyConsumer(messageType);

  /// <inheritdoc />
  public bool IsComposite(string messageType)
    => Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.IsComposite(messageType);
}
