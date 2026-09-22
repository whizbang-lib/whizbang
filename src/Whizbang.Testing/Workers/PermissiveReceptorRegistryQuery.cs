using Whizbang.Core.Messaging;

namespace Whizbang.Testing.Workers;

/// <summary>
/// An <see cref="IReceptorRegistryQuery"/> that answers yes to everything: every message has a
/// consumer, every lifecycle stage has receptors. For tests that build a worker by hand and want the
/// behavior the worker had when no registry was supplied at all, which was to gate nothing and discard
/// nothing. A real host gets the generated registry through <c>AddWhizbang()</c> instead.
/// </summary>
/// <docs>testing/multi-service-harness</docs>
public sealed class PermissiveReceptorRegistryQuery : IReceptorRegistryQuery {
  /// <inheritdoc />
  public bool HasReceptors(LifecycleStage stage, string messageType) => true;

  /// <inheritdoc />
  public bool HasInboxHandler(string messageType) => true;

  /// <inheritdoc />
  public bool HasAnyConsumer(string messageType) => true;

  /// <inheritdoc />
  public IReadOnlyList<HandledMessageInfo> GetHandledMessages() => [];
}
