// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Extractors;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Dispatcher;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// A request's scope extension and listed claim reach a receptor two services away (#1854). Service A
/// dispatches under the request scope; its outbox row crosses the JSON wire to service B, whose receptor
/// returns an event; that event's row crosses to service C, whose receptor returns another. Each receptor
/// sees the extension and the listed claim in its scope context, every emitted event carries them, and the
/// unlisted claim never leaves service A.
/// </summary>
/// <remarks>
/// B establishes its scope from the hops alone (no security provider) and C through the default provider
/// and the hop extractor, so both receive paths are crossed.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Security/HopScope.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
[Category("Security")]
public class HopScopeTwoHopWireTests {

  [Test]
  public async Task ExtensionAndListedClaim_ReachAReceptorTwoHopsAway_AndTheEventItEmitsAsync() {
    var id = (Guid)TrackedGuid.New();
    IScopeContext? seenAtB = null;
    IScopeContext? seenAtC = null;

    // Service A: the request.
    var serviceA = _service("service-a", securityProvider: false);
    ScopeContextAccessor.CurrentContext = HopScopeTests.EdgeContext();
    await serviceA.Dispatcher.SendAsync(new HopScopeParityCommand(id));
    var rowA = serviceA.Strategy.Rows.Single();
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;

    // Service B: one hop away; its receptor returns an event.
    var serviceB = _service("service-b", securityProvider: false);
    await serviceB.InvokeAsync(
      _receive(rowA, new HopScopeParityCommand(id)),
      typeof(HopScopeParityCommand),
      () => {
        seenAtB = ScopeContextAccessor.CurrentContext;
        return Route.Outbox(new HopScopeParityEvent(id));
      });
    await Assert.That(seenAtB).IsNotNull().Because("service B's receptor never ran");
    await Assert.That(serviceB.Strategy.Rows.Count).IsEqualTo(1).Because("service B's receptor emits one event");
    var rowB = serviceB.Strategy.Rows.Single();

    // Service C: two hops away; its receptor returns an event too.
    var serviceC = _service("service-c", securityProvider: true);
    await serviceC.InvokeAsync(
      _receive(rowB, new HopScopeParityEvent(id)),
      typeof(HopScopeParityEvent),
      () => {
        seenAtC = ScopeContextAccessor.CurrentContext;
        return Route.Outbox(new HopScopeParityEmittedEvent(id));
      });
    var rowC = serviceC.Strategy.Rows.Single();

    await _assertCarriesAsync(seenAtB, "the receptor one hop away");
    await _assertCarriesAsync(rowB.Envelope.GetCurrentScope(), "the event the first receptor emitted");
    await _assertCarriesAsync(seenAtC, "the receptor two hops away");
    await _assertCarriesAsync(rowC.Envelope.GetCurrentScope(), "the event the receptor two hops away emitted");
  }

  private static async Task _assertCarriesAsync(IScopeContext? scope, string where) {
    await Assert.That(scope).IsNotNull().Because($"{where} has no scope at all");
    await Assert.That(scope!.Scope.TenantId).IsEqualTo("tenant-hop");
    await Assert.That(scope.Scope.Extensions.Select(e => (e.Key, e.Value)))
      .IsEquivalentTo([("acting-admin", (string?)"true")])
      .Because($"{where} must see the extension the request mapped into its scope");
    await Assert.That(scope.Claims.Keys).IsEquivalentTo(["department"])
      .Because($"{where} sees the listed claim, and the unlisted one never left the first service");
  }

  /// <summary>The bytes a transport carries, read back the way a receiving service types its payload.</summary>
  private static MessageEnvelope<TMessage> _receive<TMessage>(OutboxMessage row, TMessage payload) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var bytes = JsonSerializer.SerializeToUtf8Bytes(row.Envelope, typeInfo);
    var wire = (MessageEnvelope<JsonElement>)JsonSerializer.Deserialize(bytes, typeInfo)!;
    return new MessageEnvelope<TMessage> {
      MessageId = wire.MessageId,
      Payload = payload,
      Hops = wire.Hops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Inbox },
    };
  }

  private static Service _service(string name, bool securityProvider) {
    var strategy = new HopScopeDispatchParityTests.CapturingStrategy();
    var registry = new SingleReceptorRegistry();
    var services = new ServiceCollection();
    // Each service is its own: a receiving service skips post-inbox receptors for its own messages.
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(TrackedGuid.New(), name, "hop-scope-host", Environment.ProcessId));
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    if (securityProvider) {
      services.AddSingleton<IMessageSecurityContextProvider>(new DefaultMessageSecurityContextProvider(
        extractors: [new MessageHopSecurityExtractor(NullLogger<MessageHopSecurityExtractor>.Instance)],
        callbacks: [],
        options: new MessageSecurityOptions()));
    }
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    return new Service(provider, provider.GetRequiredService<IDispatcher>(), strategy, registry);
  }

  private sealed record Service(ServiceProvider Provider, IDispatcher Dispatcher,
      HopScopeDispatchParityTests.CapturingStrategy Strategy, SingleReceptorRegistry Registry) {
    public async Task InvokeAsync(IMessageEnvelope envelope, Type messageType, Func<object?> handle) {
      Registry.Set(messageType, handle);
      using var scope = Provider.CreateScope();
      var invoker = new ReceptorInvoker(Registry, scope.ServiceProvider,
        new DispatcherEventCascader(scope.ServiceProvider, NullLogger<DispatcherEventCascader>.Instance));
      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);
    }
  }

  private sealed class SingleReceptorRegistry : IReceptorRegistry {
    private ReceptorInfo? _receptor;

    public void Set(Type messageType, Func<object?> handle) =>
      _receptor = new ReceptorInfo(messageType, "hop-scope-probe",
        (_, _, _, _, _) => ValueTask.FromResult(handle()));

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
      _receptor is { } r && r.MessageType == messageType && stage == LifecycleStage.PostInboxInline ? [r] : [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new NotSupportedException();

    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new NotSupportedException();

    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new NotSupportedException();

    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new NotSupportedException();
  }
}
