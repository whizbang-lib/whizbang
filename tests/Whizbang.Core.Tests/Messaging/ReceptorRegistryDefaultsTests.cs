using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Covers the default interface method bodies on <see cref="IReceptorRegistry"/>.
/// Production registries override these; the defaults are the fallback an
/// implementation inherits when it does not, so they are exercised through a
/// stub that deliberately leaves them alone.
/// </summary>
[Category("Core")]
[Category("Messaging")]
public class ReceptorRegistryDefaultsTests {
  /// <summary>Implements only the abstract members so the defaults stay inherited.</summary>
  private sealed class MinimalReceptorRegistry : IReceptorRegistry {
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage)
        => Array.Empty<ReceptorInfo>();

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage)
        where TMessage : IMessage { }

    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage)
        where TMessage : IMessage { }

    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage)
        where TMessage : IMessage => false;

    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage)
        where TMessage : IMessage => false;
  }

  [Test]
  public async Task HasRuntimeConsumerFor_WhenNotOverridden_ReturnsFalseAsync() {
    IReceptorRegistry registry = new MinimalReceptorRegistry();

    await Assert.That(registry.HasRuntimeConsumerFor("Whizbang.Tests.SomeMessage")).IsFalse();
  }

  [Test]
  public async Task HasAnyRuntimeReceptors_WhenNotOverridden_ReturnsFalseAsync() {
    IReceptorRegistry registry = new MinimalReceptorRegistry();

    await Assert.That(registry.HasAnyRuntimeReceptors("Whizbang.Tests.SomeMessage")).IsFalse();
  }
}
