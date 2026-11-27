// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Receptors;
using Whizbang.Testing.Tests.TestSupport;

namespace Whizbang.Testing.Tests.Receptors;

/// <summary>
/// <see cref="TestHandling"/> (#2609) enters the handling a receptor runs in when a unit test calls its
/// <c>HandleAsync</c> directly, so a strong id's <c>DeriveFrom(Handling.Current, purpose)</c> derives instead of throwing.
/// The receptor and service it names come from the service provider, as the invoker's do, never from a type name.
/// </summary>
/// <code-under-test>src/Whizbang.Testing/Receptors/TestHandling.cs</code-under-test>
/// <code-under-test>src/Whizbang.Testing/Receptors/TestHandlingScope.cs</code-under-test>
[NotInParallel("TestHandling")] // the handling is ambient: a test that reads Handling.Current must not see another's
[System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S6966:Awaitable method should be used", Justification = "The scope's synchronous Dispose is part of the API under test, beside DisposeAsync.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1261:Resource can be disposed asynchronously", Justification = "The scope's synchronous Dispose is part of the API under test, beside DisposeAsync.")]
public class TestHandlingTests {
  // The inputs of StrongIdDerivationTests.Derive_FixedInputs_DeriveTheCompatibilityValueAsync, so the handling this
  // helper enters is checked against the derivation's fixed value, not against itself
  private const string PIN = "3f2c8a1e-5b7d-4c9e-a1f0-6d2b8e4c7a91";
  private const string SERVICE = "orders-service";
  private const string HANDLER = "OrderPlacedHandler";
  private const string RECEPTOR = "App.OrderReceptor";
  private static readonly Guid _cause = Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-2c3d4e5f6a7b");
  private static readonly Guid _compatibilityValue = Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-1123cd895f8b");

  [Test]
  public async Task Enter_AnEnvelope_EntersTheHandlingOfTheRegisteredReceptorInItsServiceAsync() {
    var receptor = new ProbeReceptor();
    var services = _services(_compiled(RECEPTOR, LifecycleStage.LocalImmediateInline, LifecycleStage.PostInboxDetached), SERVICE);

    Guid id;
    await using (var scope = TestHandling.Enter(services, receptor, _envelope(_cause, HANDLER))) {
      await Assert.That(Handling.Current).IsSameReferenceAs(scope.Handling);
      id = StrongIdDerivation.Derive(Handling.Current, PIN, "order", "OrderId");
    }

    await Assert.That(id).IsEqualTo(_compatibilityValue)
      .Because("the handled message, its handler, the service and the registry's receptor id are the derivation's inputs");
  }

  [Test]
  public async Task Enter_AMessage_EntersAFreshLocalEnvelopeOfItAsync() {
    var receptor = new ProbeReceptor();
    var message = new TestEvent("placed");
    var services = _services(_compiled(RECEPTOR, LifecycleStage.LocalImmediateInline), SERVICE);

    using var first = TestHandling.Enter(services, receptor, message);
    var firstId = first.Envelope.MessageId;
    first.Dispose();
    using var second = TestHandling.Enter(services, receptor, message);

    await Assert.That(first.Envelope.Payload).IsSameReferenceAs(message);
    await Assert.That(first.Envelope.DispatchContext.Mode).IsEqualTo(DispatchModes.Local);
    await Assert.That(first.Envelope.DispatchContext.Source).IsEqualTo(MessageSource.Local);
    await Assert.That(first.Envelope.DispatchContext.HandlerName).IsNull();
    await Assert.That(firstId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(second.Envelope.MessageId).IsNotEqualTo(firstId)
      .Because("each handling of a message given without an envelope is a new message, as a fresh dispatch is");
    await Assert.That(Handling.Current.IsActive).IsTrue();
  }

  [Test]
  public async Task Enter_TheScopesEnvelopeAgain_IsARetry_AndDerivesTheSameIdAsync() {
    var receptor = new ProbeReceptor();
    var services = _services(_compiled(RECEPTOR, LifecycleStage.LocalImmediateInline), SERVICE);

    Guid first;
    Guid retry;
    IMessageEnvelope envelope;
    using (var scope = TestHandling.Enter(services, receptor, new TestEvent("placed"))) {
      envelope = scope.Envelope;
      first = StrongIdDerivation.Derive(Handling.Current, PIN, "order", "OrderId");
    }
    using (TestHandling.Enter(services, receptor, envelope)) {
      retry = StrongIdDerivation.Derive(Handling.Current, PIN, "order", "OrderId");
    }

    await Assert.That(retry).IsEqualTo(first);
  }

  [Test]
  public async Task Dispose_EndsTheHandling_AndRestoresTheOneBeforeItAsync() {
    var receptor = new ProbeReceptor();
    var services = _services(_compiled(RECEPTOR, LifecycleStage.LocalImmediateInline), SERVICE);

    var outer = TestHandling.Enter(services, receptor, new TestEvent("outer"));
    var inner = TestHandling.Enter(services, receptor, new TestEvent("inner"));
    var insideInner = Handling.Current;
    await inner.DisposeAsync();
    var backInOuter = Handling.Current;
    outer.Dispose();

    await Assert.That(insideInner).IsSameReferenceAs(inner.Handling);
    await Assert.That(backInOuter).IsSameReferenceAs(outer.Handling);
    await Assert.That(Handling.Current.IsActive).IsFalse()
      .Because("after the last scope is disposed the test is outside any handling, as it was before");
    await Assert.That(() => StrongIdDerivation.Derive(inner.Handling, PIN, "order", "OrderId"))
      .Throws<InvalidOperationException>().WithMessageContaining("handling that has ended");
  }

  [Test]
  public async Task Dispose_ASecondTime_DoesNotUndoAHandlingEnteredSinceAsync() {
    var receptor = new ProbeReceptor();
    var services = _services(_compiled(RECEPTOR, LifecycleStage.LocalImmediateInline), SERVICE);

    var first = TestHandling.Enter(services, receptor, new TestEvent("first"));
    first.Dispose();
    using var second = TestHandling.Enter(services, receptor, new TestEvent("second"));
    first.Dispose();
    await first.DisposeAsync();

    await Assert.That(Handling.Current).IsSameReferenceAs(second.Handling);
  }

  [Test]
  public async Task Enter_ARuntimeRegistration_IsMatchedByItsInstanceAsync() {
    var registered = new ProbeReceptor();
    var other = new ProbeReceptor();
    var runtimeId = "runtime_Probe_" + LifecycleStage.PostInboxInline;
    var services = _services(
      _registry((LifecycleStage.PostInboxInline, _info(runtimeId, receptorType: null, runtimeInstance: registered))), SERVICE);

    Guid viaHelper;
    using (TestHandling.Enter(services, registered, _envelope(_cause, HANDLER))) {
      viaHelper = StrongIdDerivation.Derive(Handling.Current, PIN, "order", "OrderId");
    }
    var otherInstance = await Assert.That(() => TestHandling.Enter(services, other, _envelope(_cause, HANDLER)))
      .Throws<InvalidOperationException>();

    await Assert.That(viaHelper).IsNotEqualTo(_compatibilityValue)
      .Because("the runtime registration's own receptor id is the one derived from");
    await Assert.That(otherInstance!.Message).Contains("is not registered");
  }

  [Test]
  public async Task Enter_ACompiledEntry_IsNotMatchedByARuntimeInstanceOfAnotherRegistrationAsync() {
    // A runtime registration of a receptor of the same class is another receptor: its entry carries the instance, so a
    // different instance of that class matches only a compiled entry (one with no instance)
    var registered = new ProbeReceptor();
    var services = _services(
      _registry((LifecycleStage.PostInboxInline, _info("runtime_Probe", typeof(ProbeReceptor), runtimeInstance: registered))), SERVICE);

    await Assert.That(() => TestHandling.Enter(services, new ProbeReceptor(), _envelope(_cause, HANDLER)))
      .Throws<InvalidOperationException>().WithMessageContaining("is not registered");
  }

  [Test]
  public async Task Enter_AReceptorUnderTwoIds_NeedsTheStage_AndTakesThatStagesIdAsync() {
    var receptor = new ProbeReceptor();
    var services = _services(_registry(
      (LifecycleStage.PreOutboxInline, _info("runtime_Probe_PreOutboxInline", receptorType: null, runtimeInstance: receptor)),
      (LifecycleStage.PostInboxInline, _info(RECEPTOR, receptorType: null, runtimeInstance: receptor))), SERVICE);

    var ambiguous = await Assert.That(() => TestHandling.Enter(services, receptor, _envelope(_cause, HANDLER)))
      .Throws<InvalidOperationException>();
    Guid atPostInbox;
    using (TestHandling.Enter(services, receptor, _envelope(_cause, HANDLER), LifecycleStage.PostInboxInline)) {
      atPostInbox = StrongIdDerivation.Derive(Handling.Current, PIN, "order", "OrderId");
    }

    await Assert.That(ambiguous!.Message).Contains("runtime_Probe_PreOutboxInline").And.Contains(RECEPTOR)
      .And.Contains("pass the stage");
    await Assert.That(atPostInbox).IsEqualTo(_compatibilityValue);
  }

  [Test]
  public async Task Enter_AStageTheReceptorIsNotRegisteredAt_IsRefusedNamingTheStageAsync() {
    var services = _services(_compiled(RECEPTOR, LifecycleStage.LocalImmediateInline), SERVICE);

    await Assert.That(() => TestHandling.Enter(services, new ProbeReceptor(), new TestEvent("x"), LifecycleStage.PostInboxInline))
      .Throws<InvalidOperationException>().WithMessageContaining("at PostInboxInline");
  }

  [Test]
  public async Task Enter_AReceptorRegisteredForAnotherMessage_IsRefusedAsync() {
    var services = _services(_compiled(RECEPTOR, LifecycleStage.LocalImmediateInline), SERVICE);

    var thrown = await Assert.That(() => TestHandling.Enter(services, new ProbeReceptor(), new TestCommand("other")))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains(nameof(ProbeReceptor)).And.Contains(nameof(TestCommand))
      .And.Contains("is not registered");
  }

  [Test]
  public async Task Enter_NoReceptorRegistry_IsRefusedNamingHowToAddOneAsync() {
    await using var services = new ServiceCollection().BuildServiceProvider();

    await Assert.That(() => TestHandling.Enter(services, new ProbeReceptor(), new TestEvent("x")))
      .Throws<InvalidOperationException>().WithMessageContaining("AddWhizbangReceptorRegistry");
  }

  [Test]
  public async Task Enter_NoServiceInstance_NamesNoService_AsTheInvokerDoesAsync() {
    var receptor = new ProbeReceptor();
    var named = _services(_compiled(RECEPTOR, LifecycleStage.LocalImmediateInline), SERVICE);
    var unnamed = _services(_compiled(RECEPTOR, LifecycleStage.LocalImmediateInline), serviceName: null);

    Guid inNamed;
    Guid inUnnamed;
    using (TestHandling.Enter(named, receptor, _envelope(_cause, HANDLER))) {
      inNamed = StrongIdDerivation.Derive(Handling.Current, PIN, "order", "OrderId");
    }
    using (TestHandling.Enter(unnamed, receptor, _envelope(_cause, HANDLER))) {
      inUnnamed = StrongIdDerivation.Derive(Handling.Current, PIN, "order", "OrderId");
    }

    await Assert.That(inUnnamed).IsNotEqualTo(inNamed)
      .Because("the service is a derivation input, and a provider with no service instance names none");
  }

  [Test]
  public async Task Enter_NullArguments_AreRefusedAsync() {
    var services = _services(_compiled(RECEPTOR, LifecycleStage.LocalImmediateInline), SERVICE);
    var receptor = new ProbeReceptor();
    var envelope = _envelope(_cause, HANDLER);

    await Assert.That(() => TestHandling.Enter(null!, receptor, new TestEvent("x"))).Throws<ArgumentNullException>();
    await Assert.That(() => TestHandling.Enter(services, null!, new TestEvent("x"))).Throws<ArgumentNullException>();
    await Assert.That(() => TestHandling.Enter(services, receptor, (object)null!)).Throws<ArgumentNullException>();
    await Assert.That(() => TestHandling.Enter(null!, receptor, envelope)).Throws<ArgumentNullException>();
    await Assert.That(() => TestHandling.Enter(services, null!, envelope)).Throws<ArgumentNullException>();
    await Assert.That(() => TestHandling.Enter(services, receptor, (IMessageEnvelope)null!)).Throws<ArgumentNullException>();
  }

  private static ServiceProvider _services(IReceptorRegistry registry, string? serviceName) {
    var services = new ServiceCollection().AddSingleton(registry);
    if (serviceName is not null) {
      services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.Empty, serviceName, "test-host", 1));
    }
    return services.BuildServiceProvider();
  }

  // A compiled receptor: one id, listed at every stage it fires at, identified by its class
  private static ListedReceptorRegistry _compiled(string receptorId, params LifecycleStage[] stages) =>
    _registry([.. stages.Select(stage => (stage, _info(receptorId, typeof(ProbeReceptor), runtimeInstance: null)))]);

  private static ListedReceptorRegistry _registry(params (LifecycleStage Stage, ReceptorInfo Info)[] entries) => new(entries);

  private static ReceptorInfo _info(string receptorId, Type? receptorType, object? runtimeInstance) => new(
    MessageType: typeof(TestEvent),
    ReceptorId: receptorId,
    InvokeAsync: (_, _, _, _, _) => ValueTask.FromResult<object?>(null),
    RuntimeInstance: runtimeInstance,
    ReceptorType: receptorType);

  private static MessageEnvelope<TestEvent> _envelope(Guid messageId, string handlerName) => new() {
    MessageId = new MessageId(messageId),
    Payload = new TestEvent("placed"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local, HandlerName = handlerName },
  };

  /// <summary>The receptor a test handling is entered for.</summary>
  private sealed class ProbeReceptor : IReceptor<TestEvent> {
    public ValueTask HandleAsync(TestEvent message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  /// <summary>A registry listing fixed entries per stage, for the receptor <see cref="TestEvent"/> is handled by.</summary>
  private sealed class ListedReceptorRegistry((LifecycleStage Stage, ReceptorInfo Info)[] entries) : IReceptorRegistry {
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
      [.. entries.Where(e => e.Stage == stage && e.Info.MessageType == messageType).Select(e => e.Info)];

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
