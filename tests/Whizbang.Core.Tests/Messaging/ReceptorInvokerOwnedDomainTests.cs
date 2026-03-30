using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests that ReceptorInvoker respects owned-domain rules at each lifecycle stage.
/// A handler registered at all 3 default stages should only fire at the stage
/// appropriate for the message's ownership status:
///
/// <code>
/// Stage               | Owned Event | Non-owned Event | Owned Cmd | Non-owned Cmd
/// --------------------|:-----------:|:---------------:|:---------:|:------------:
/// LocalImmediateInline| Fire        | Fire            | Fire      | Fire
/// PreOutboxInline     | Fire        | Skip            | Skip      | Fire
/// PostInboxInline     | Skip        | Fire            | Fire      | Skip
/// </code>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
/// <docs>fundamentals/dispatcher/routing#owned-domain-routing</docs>
public class ReceptorInvokerOwnedDomainTests {

  // Test messages — in this test namespace (Whizbang.Core.Tests.Messaging)
  // When OwnDomains includes this namespace, they are "owned"
  private sealed record OwnedEvent(Guid Id) : IEvent;       // IEvent = event
  private sealed record OwnedCommand(string Data) : IMessage; // IMessage (not IEvent) = command

  private sealed class InvocationTracker {
    private readonly List<(string ReceptorId, LifecycleStage Stage)> _invocations = [];
    public IReadOnlyList<(string ReceptorId, LifecycleStage Stage)> Invocations => _invocations;
    public void Record(string receptorId, LifecycleStage stage) => _invocations.Add((receptorId, stage));
  }

  private sealed class TestReceptorRegistry(InvocationTracker tracker) : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void Register<TMessage>(string receptorId, LifecycleStage stage) {
      var key = (typeof(TMessage), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(new ReceptorInfo(
        MessageType: typeof(TMessage),
        ReceptorId: receptorId,
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          tracker.Record(receptorId, stage);
          return ValueTask.FromResult<object?>(null);
        }
      ));
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : [];
    }

    // Not needed for these tests — stub implementations
    void IReceptorRegistry.Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) { }
    void IReceptorRegistry.Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) { }
    bool IReceptorRegistry.Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) => false;
    bool IReceptorRegistry.Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) => false;
  }

  private static MessageEnvelope<T> _wrap<T>(T message) where T : notnull {
    return new MessageEnvelope<T> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = message,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static ReceptorInvoker _createInvoker(IReceptorRegistry registry, string[] ownedDomains) {
    var services = new ServiceCollection();
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains(ownedDomains);
    services.AddSingleton<IOptions<RoutingOptions>>(Options.Create(routingOptions));
    var sp = services.BuildServiceProvider();
    return new ReceptorInvoker(registry, sp);
  }

  // ========================================
  // LocalImmediateInline — always fires
  // ========================================

  [Test]
  public async Task LocalImmediateInline_OwnedEvent_FiresAsync() {
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.Register<OwnedEvent>("handler", LifecycleStage.LocalImmediateInline);
    var invoker = _createInvoker(registry, ["Whizbang.Core.Tests.Messaging"]);

    await invoker.InvokeAsync(_wrap(new OwnedEvent(Guid.NewGuid())), LifecycleStage.LocalImmediateInline);

    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  [Test]
  public async Task LocalImmediateInline_OwnedCommand_FiresAsync() {
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.Register<OwnedCommand>("handler", LifecycleStage.LocalImmediateInline);
    var invoker = _createInvoker(registry, ["Whizbang.Core.Tests.Messaging"]);

    await invoker.InvokeAsync(_wrap(new OwnedCommand("test")), LifecycleStage.LocalImmediateInline);

    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  // ========================================
  // PreOutboxInline — owned events fire, owned commands skip
  // ========================================

  [Test]
  public async Task PreOutboxInline_OwnedEvent_FiresAsync() {
    // Owned events go to transport (other services subscribe) → handler fires
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.Register<OwnedEvent>("handler", LifecycleStage.PreOutboxInline);
    var invoker = _createInvoker(registry, ["Whizbang.Core.Tests.Messaging"]);

    await invoker.InvokeAsync(_wrap(new OwnedEvent(Guid.NewGuid())), LifecycleStage.PreOutboxInline);

    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PreOutboxInline_OwnedCommand_SkipsAsync() {
    // Owned commands stay local → should NOT fire at PreOutboxInline
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.Register<OwnedCommand>("handler", LifecycleStage.PreOutboxInline);
    var invoker = _createInvoker(registry, ["Whizbang.Core.Tests.Messaging"]);

    await invoker.InvokeAsync(_wrap(new OwnedCommand("test")), LifecycleStage.PreOutboxInline);

    await Assert.That(tracker.Invocations).Count().IsEqualTo(0);
  }

  [Test]
  public async Task PreOutboxInline_NonOwnedEvent_SkipsAsync() {
    // Non-owned events shouldn't be published by this service → skip
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.Register<OwnedEvent>("handler", LifecycleStage.PreOutboxInline);
    var invoker = _createInvoker(registry, ["SomeOther.Namespace"]); // NOT owned

    await invoker.InvokeAsync(_wrap(new OwnedEvent(Guid.NewGuid())), LifecycleStage.PreOutboxInline);

    await Assert.That(tracker.Invocations).Count().IsEqualTo(0);
  }

  [Test]
  public async Task PreOutboxInline_NonOwnedCommand_FiresAsync() {
    // Non-owned commands going to another service's inbox → handler fires
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.Register<OwnedCommand>("handler", LifecycleStage.PreOutboxInline);
    var invoker = _createInvoker(registry, ["SomeOther.Namespace"]); // NOT owned

    await invoker.InvokeAsync(_wrap(new OwnedCommand("test")), LifecycleStage.PreOutboxInline);

    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  // ========================================
  // PostInboxInline — non-owned events fire, owned events skip (self-echo)
  // ========================================

  [Test]
  public async Task PostInboxInline_OwnedEvent_SkipsAsync() {
    // Owned event arriving via inbox = self-echo → skip
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.Register<OwnedEvent>("handler", LifecycleStage.PostInboxInline);
    var invoker = _createInvoker(registry, ["Whizbang.Core.Tests.Messaging"]);

    await invoker.InvokeAsync(_wrap(new OwnedEvent(Guid.NewGuid())), LifecycleStage.PostInboxInline);

    await Assert.That(tracker.Invocations).Count().IsEqualTo(0);
  }

  [Test]
  public async Task PostInboxInline_NonOwnedEvent_FiresAsync() {
    // Non-owned event from another service → handler fires
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.Register<OwnedEvent>("handler", LifecycleStage.PostInboxInline);
    var invoker = _createInvoker(registry, ["SomeOther.Namespace"]); // NOT owned

    await invoker.InvokeAsync(_wrap(new OwnedEvent(Guid.NewGuid())), LifecycleStage.PostInboxInline);

    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PostInboxInline_OwnedCommand_FiresAsync() {
    // Owned command from another service → handler fires (we own the receptor)
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.Register<OwnedCommand>("handler", LifecycleStage.PostInboxInline);
    var invoker = _createInvoker(registry, ["Whizbang.Core.Tests.Messaging"]);

    await invoker.InvokeAsync(_wrap(new OwnedCommand("test")), LifecycleStage.PostInboxInline);

    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PostInboxInline_NonOwnedCommand_SkipsAsync() {
    // Non-owned command arriving at this service = routing error → skip
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.Register<OwnedCommand>("handler", LifecycleStage.PostInboxInline);
    var invoker = _createInvoker(registry, ["SomeOther.Namespace"]); // NOT owned

    await invoker.InvokeAsync(_wrap(new OwnedCommand("test")), LifecycleStage.PostInboxInline);

    await Assert.That(tracker.Invocations).Count().IsEqualTo(0);
  }

  // ========================================
  // No owned domains configured — all stages fire (backward compat)
  // ========================================

  [Test]
  public async Task NoOwnedDomains_AllStagesFire_BackwardCompatAsync() {
    // Without owned domains configured, all stages fire (backward compatibility)
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.Register<OwnedEvent>("handler", LifecycleStage.LocalImmediateInline);
    registry.Register<OwnedEvent>("handler", LifecycleStage.PreOutboxInline);
    registry.Register<OwnedEvent>("handler", LifecycleStage.PostInboxInline);

    // No routing options = no owned domains = no filtering
    var sp = new ServiceCollection().BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, sp);

    await invoker.InvokeAsync(_wrap(new OwnedEvent(Guid.NewGuid())), LifecycleStage.LocalImmediateInline);
    await invoker.InvokeAsync(_wrap(new OwnedEvent(Guid.NewGuid())), LifecycleStage.PreOutboxInline);
    await invoker.InvokeAsync(_wrap(new OwnedEvent(Guid.NewGuid())), LifecycleStage.PostInboxInline);

    await Assert.That(tracker.Invocations).Count().IsEqualTo(3);
  }
}
