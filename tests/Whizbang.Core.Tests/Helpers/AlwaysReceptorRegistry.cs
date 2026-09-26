using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Helpers;

/// <summary>
/// A runtime receptor registry that reports one receptor for every type it is asked about, and
/// records every question. Workers consult a runtime registry with a type that a wire type name
/// may not have resolved to; a registry that always answers "yes" is what makes the difference
/// between "the guard short-circuited" and "the registry happened to have nothing" visible.
/// </summary>
internal sealed class AlwaysReceptorRegistry : IReceptorRegistry {
  private readonly List<(Type MessageType, LifecycleStage Stage)> _questions = [];

  /// <summary>Every lookup this registry was asked to perform, in order.</summary>
  public IReadOnlyList<(Type MessageType, LifecycleStage Stage)> Questions {
    get { lock (_questions) { return [.. _questions]; } }
  }

  public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
    ArgumentNullException.ThrowIfNull(messageType);
    lock (_questions) {
      _questions.Add((messageType, stage));
    }
    return [
      new ReceptorInfo(
        messageType,
        $"always-{messageType.Name}-{stage}",
        (_, _, _, _, _) => ValueTask.FromResult<object?>(null))
    ];
  }

  public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage
    => throw new NotSupportedException("This registry answers lookups only.");

  public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage
    => throw new NotSupportedException("This registry answers lookups only.");

  public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;

  public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
}
