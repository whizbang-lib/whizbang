// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// #1996: the envelope a receptor interceptor is shown on a local path. The envelope in hand when it carries this
/// message; otherwise the one the dispatcher registered for the message; otherwise one built for the question, which
/// keeps the hops of the envelope in hand so the scope they carry still reaches the interceptor.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Security/ReceptorDenial.cs</code-under-test>
[Category("Security")]
public class ReceptorAdmissionEnvelopeTests {
  private sealed record Probe(Guid Id);
  private sealed class ProbeReceptor;

  [Test]
  public async Task EnvelopeInHandCarryingTheMessage_IsShownAsItIsAsync() {
    var (dispatcher, interceptor, _) = _create(withRegistry: true);
    var message = new Probe(TrackedGuid.New());
    var envelope = _envelope<object>(message);

    await dispatcher.AdmitAsync(message, envelope);

    await Assert.That(interceptor.Envelope).IsSameReferenceAs(envelope);
  }

  [Test]
  public async Task EnvelopeInHandForAnotherMessage_TheRegisteredEnvelopeIsShownAsync() {
    var (dispatcher, interceptor, registry) = _create(withRegistry: true);
    var message = new Probe(TrackedGuid.New());
    var registered = _envelope(message);
    registry!.Register(registered);

    await dispatcher.AdmitAsync(message, _envelope<object>(new Probe(TrackedGuid.New())));

    await Assert.That(interceptor.Envelope).IsSameReferenceAs(registered);
  }

  [Test]
  public async Task EnvelopeInHandForAnotherMessage_NothingRegistered_BuildsOneWithItsHopsAsync() {
    var (dispatcher, interceptor, _) = _create(withRegistry: true);
    var message = new Probe(TrackedGuid.New());
    var source = _envelope<object>(new Probe(TrackedGuid.New()));
    source.Hops.Add(new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UnixEpoch, ServiceInstance = ServiceInstanceInfo.Unknown });

    await dispatcher.AdmitAsync(message, source);

    await Assert.That(interceptor.Envelope!.Payload).IsSameReferenceAs(message);
    await Assert.That(interceptor.Envelope.Hops).Count().IsEqualTo(1)
      .Because("the source's hops carry the scope a cascaded message is handled under");
  }

  [Test]
  public async Task NoEnvelopeAndNoRegistry_BuildsOneWithNoHopsAsync() {
    var (dispatcher, interceptor, _) = _create(withRegistry: false);
    var message = new Probe(TrackedGuid.New());

    await dispatcher.AdmitAsync(message, null);

    await Assert.That(interceptor.Envelope!.Payload).IsSameReferenceAs(message);
    await Assert.That(interceptor.Envelope.Hops).IsEmpty();
  }

  [Test]
  public async Task ReasonOf_SaysSoWhenTheInterceptorGaveNoneAsync() {
    await Assert.That(ReceptorDenial.ReasonOf(InterceptorResult.Deny(DeniedAction.DeadLetter))).IsEqualTo("no reason given");
    await Assert.That(ReceptorDenial.ReasonOf(InterceptorResult.Deny(DeniedAction.DeadLetter, "why"))).IsEqualTo("why");
  }

  private static MessageEnvelope<T> _envelope<T>(T message) where T : notnull => new() {
    MessageId = MessageId.New(),
    Payload = message,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static (ProbeDispatcher Dispatcher, RecordingInterceptor Interceptor, IEnvelopeRegistry? Registry) _create(bool withRegistry) {
    var interceptor = new RecordingInterceptor();
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorInterceptor>(interceptor);
    if (withRegistry) {
      services.AddSingleton<IEnvelopeRegistry, EnvelopeRegistry>();
    }
    var provider = services.BuildServiceProvider();
    return (new ProbeDispatcher(provider), interceptor, provider.GetService<IEnvelopeRegistry>());
  }

  private sealed class RecordingInterceptor : IReceptorInterceptor {
    public IMessageEnvelope? Envelope { get; private set; }

    public ValueTask<InterceptorResult> CanInvokeAsync(Type receptorType, IMessageEnvelope envelope, IScopeContext? context,
        CancellationToken cancellationToken = default) {
      Envelope = envelope;
      return new ValueTask<InterceptorResult>(InterceptorResult.Allowed);
    }
  }

  /// <summary>Exposes the protected admission call; no receptor lookups are needed for it.</summary>
  private sealed class ProbeDispatcher(IServiceProvider serviceProvider)
      : Core.Dispatcher(serviceProvider, new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build())) {
    private readonly IServiceProvider _provider = serviceProvider;

    public ValueTask<bool> AdmitAsync(Probe message, IMessageEnvelope? envelope) =>
      AdmitReceptorAsync(typeof(ProbeReceptor), message, _provider, envelope, CancellationToken.None);

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;
  }
}
