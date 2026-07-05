using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Testing.Tests.TestSupport;

/// <summary>
/// Recording <see cref="IReceptorRegistry"/> test double. Captures runtime registrations so
/// tests can drive the registered receptors directly and assert unregistration on dispose.
/// </summary>
internal sealed class FakeReceptorRegistry : IReceptorRegistry {
  public List<(Type MessageType, object Receptor, LifecycleStage Stage)> Registered { get; } = [];
  public List<(Type MessageType, object Receptor, LifecycleStage Stage)> Unregistered { get; } = [];

  public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];

  public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage {
    Registered.Add((typeof(TMessage), receptor, stage));
  }

  public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage {
    Registered.Add((typeof(TMessage), receptor, stage));
  }

  public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage {
    Unregistered.Add((typeof(TMessage), receptor, stage));
    return Registered.RemoveAll(r => ReferenceEquals(r.Receptor, receptor) && r.Stage == stage) > 0;
  }

  public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage {
    Unregistered.Add((typeof(TMessage), receptor, stage));
    return Registered.RemoveAll(r => ReferenceEquals(r.Receptor, receptor) && r.Stage == stage) > 0;
  }

  /// <summary>Gets the single registered receptor for <typeparamref name="TMessage"/>.</summary>
  public IReceptor<TMessage> GetSingleReceptor<TMessage>() where TMessage : IMessage {
    return (IReceptor<TMessage>)Registered.Single(r => r.MessageType == typeof(TMessage)).Receptor;
  }
}
