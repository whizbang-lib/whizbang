// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.ExecutorShapes.Owned;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.PolicyBehavior;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// The overloads and paths of one kind of call behave alike (#1410 follow-up): every LocalInvoke
/// overload reaches the same receptors and extracts a response the same way, a receptor's route and
/// default routing hold on every path, the outbox copy is marked and collected the same way, and
/// every entry honors the schema gate and the caller's context. Real generated dispatcher.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision-table</docs>
public class DispatchExecutorParityComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.ExecutorShapes.Owned";

  // ── E6: every LocalInvoke overload reaches the same receptors and extracts a response alike ──

  [Test]
  public async Task LocalInvokeTypedWithContext_ASyncResultReceptor_ReturnsItsResponseAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExSyncRpcCommand(Guid.CreateVersion7());

    var response = await _dispatcher(provider).LocalInvokeAsync<ExSyncRpcCommand, ExRpcResponse>(command, _context());

    await Assert.That(response.Id).IsEqualTo(command.Id);
    await Assert.That(PolicyProbe.Count(nameof(ExSyncRpcCommandReceptor), command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeObject_ATupleReceptor_ReturnsTheResponseAndCascadesTheRestAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExRpcCommand(Guid.CreateVersion7());

    var response = await _dispatcher(provider).LocalInvokeAsync<ExRpcResponse>((object)command);

    await _assertRpcAsync(response, command.Id);
  }

  [Test]
  public async Task LocalInvokeObjectWithOptions_ATupleReceptor_ReturnsTheResponseAndCascadesTheRestAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExRpcCommand(Guid.CreateVersion7());

    var response = await _dispatcher(provider).LocalInvokeAsync<ExRpcResponse>((object)command, new DispatchOptions());

    await _assertRpcAsync(response, command.Id);
  }

  [Test]
  public async Task LocalInvokeTypedWithContext_ATupleReceptor_ReturnsTheResponseAndCascadesTheRestAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExRpcCommand(Guid.CreateVersion7());

    var response = await _dispatcher(provider).LocalInvokeAsync<ExRpcCommand, ExRpcResponse>(command, _context());

    await _assertRpcAsync(response, command.Id);
  }

  [Test]
  public async Task LocalInvokeWithReceipt_ATupleReceptor_ReturnsTheResponseAndCascadesTheRestAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExRpcCommand(Guid.CreateVersion7());

    var result = await _dispatcher(provider).LocalInvokeWithReceiptAsync<ExRpcResponse>((object)command);

    await _assertRpcAsync(result.Value, command.Id);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAndOptions_ATupleReceptor_ReturnsTheResponseAndCascadesTheRestAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExRpcCommand(Guid.CreateVersion7());

    var result = await _dispatcher(provider).LocalInvokeWithReceiptAsync<ExRpcResponse>((object)command, new DispatchOptions());

    await _assertRpcAsync(result.Value, command.Id);
  }

  [Test]
  public async Task LocalInvokeObject_ATupleReceptorWithDefaultRouting_CascadesTheRestByThatRouteAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new ExRpcDefaultLocalCommand(Guid.CreateVersion7());

    var response = await _dispatcher(provider).LocalInvokeAsync<ExRpcResponse>((object)command);

    await Assert.That(response.Id).IsEqualTo(command.Id);
    await Assert.That(PolicyProbe.Count(nameof(ExRpcDefaultLocalCommandReceptor), command.Id)).IsEqualTo(1);
    await _assertRanHereAndNeverTransportedAsync(outbox, command.Id);
    await Assert.That(outbox.QueuedOf(typeof(ExRpcResponse))).IsEmpty()
      .Because("the response goes back to the caller; it is never cascaded");
  }

  // ── E13: a receptor's [DefaultRouting] holds at a worker stage as on a local call ─────────────

  [Test]
  public async Task Send_ReceptorWithDefaultRoutingLocal_PersistsTheResultWithoutTransportingItAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var trigger = new ExDefaultLocalTrigger(Guid.CreateVersion7());

    await _dispatcher(provider).SendAsync(trigger);

    await _assertRanHereAndNeverTransportedAsync(outbox, trigger.Id);
  }

  [Test]
  public async Task PostInbox_ReceptorWithDefaultRoutingLocal_PersistsTheResultWithoutTransportingItAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var trigger = new ExDefaultLocalTrigger(Guid.CreateVersion7());

    await _invokeAtStageAsync(provider, trigger, LifecycleStage.PostInboxDetached);

    await _assertRanHereAndNeverTransportedAsync(outbox, trigger.Id);
  }

  // ── E21: the dynamic outbox path marks a copy as the decision says ───────────────────────────

  [Test]
  public async Task Send_RouteOutboxResultOfAnUndeclaredType_QueuesAnUnmarkedCopyAndDoesNotRunItHereAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var trigger = new ExObjectRouteOutboxTrigger(Guid.CreateVersion7());

    await _dispatcher(provider).SendAsync(trigger);

    await Assert.That(PolicyProbe.Count(nameof(ExUndeclaredEventReceptor), trigger.Id)).IsEqualTo(0)
      .Because("Route.Outbox does not run the result here");
    var queued = outbox.QueuedOf(typeof(ExUndeclaredEvent));
    await Assert.That(queued).Count().IsEqualTo(1);
    await Assert.That(queued[0].Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsFalse()
      .Because("a copy this service did not handle must not claim it did, or the receiver's echo rules skip it");
  }

  // ── E23: a send inside a collecting scope joins the scope's commit ───────────────────────────

  [Test]
  public async Task Send_InsideACollectingScope_CollectsTheCopyInsteadOfQueuingItDirectlyAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbUnownedCommand(Guid.CreateVersion7());

    using var collecting = DispatchOutboxCollector.BeginCollecting();
    await _dispatcher(provider).SendAsync(command);

    await Assert.That(collecting.Collector.Collected.Count(m => m.MessageType.Contains(nameof(PbUnownedCommand), StringComparison.Ordinal))).IsEqualTo(1)
      .Because("a send inside a collecting scope commits atomically with the scope, as a publish does");
    await Assert.That(outbox.QueuedOf(typeof(PbUnownedCommand))).IsEmpty();
  }

  [Test]
  public async Task SendMany_InsideACollectingScope_CollectsTheCopiesInsteadOfQueuingThemDirectlyAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbUnownedCommand(Guid.CreateVersion7());

    using var collecting = DispatchOutboxCollector.BeginCollecting();
    await _dispatcher(provider).SendManyAsync(new object[] { command });

    await Assert.That(collecting.Collector.Collected.Count(m => m.MessageType.Contains(nameof(PbUnownedCommand), StringComparison.Ordinal))).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(PbUnownedCommand))).IsEmpty();
  }

  // ── E26: every entry refuses while the schema is not ready ───────────────────────────────────

  [Test]
  public async Task EveryEntry_WhileTheSchemaIsNotReady_RefusesBeforeRunningAnythingAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox, services => services.AddSingleton<ISchemaReadyGate>(new ClosedSchemaGate()));
    var dispatcher = _dispatcher(provider);
    var id = Guid.CreateVersion7();

    var entries = new (string Name, Func<Task> Call)[] {
      ("SendAsync", () => dispatcher.SendAsync(new ExVoidCommand(id))),
      ("SendManyAsync", () => dispatcher.SendManyAsync(new object[] { new ExVoidCommand(id) })),
      ("LocalInvokeAsync(void)", async () => await dispatcher.LocalInvokeAsync((object)new ExVoidCommand(id))),
      ("LocalInvokeAsync<TResult>", async () => await dispatcher.LocalInvokeAsync<ExRpcResponse>((object)new ExRpcCommand(id))),
      ("LocalInvokeWithReceiptAsync", async () => await dispatcher.LocalInvokeWithReceiptAsync<ExRpcResponse>((object)new ExRpcCommand(id))),
      ("LocalSendManyAsync", async () => await dispatcher.LocalSendManyAsync(new object[] { new ExVoidCommand(id) })),
      ("PublishAsync", () => dispatcher.PublishAsync(new ExVoidEvent(id))),
      ("PublishManyAsync", () => dispatcher.PublishManyAsync(new object[] { new ExVoidEvent(id) })),
      ("CascadeMessageAsync", () => dispatcher.CascadeMessageAsync(new ExVoidEvent(id), sourceEnvelope: null, DispatchModes.Both)),
    };

    foreach (var (name, call) in entries) {
      await Assert.That(call).ThrowsExactly<WhizbangNotReadyException>().Because($"{name} writes, so it refuses while the schema is not ready");
    }
    await Assert.That(PolicyProbe.Count(nameof(ExVoidCommandReceptor), id)).IsEqualTo(0);
    await Assert.That(PolicyProbe.Count(nameof(ExVoidEventReceptor), id)).IsEqualTo(0);
    await Assert.That(PolicyProbe.Count(nameof(ExRpcCommandReceptor), id)).IsEqualTo(0);
  }

  // ── E27: a call that needs a result refuses without a handler, ContinueWhenNoHandler or not ──

  [Test]
  public async Task LocalInvokeObjectWithOptions_ContinueWhenNoHandler_StillRefusesBecauseAResultIsNeededAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var unhandled = new PbUnhandledCommand(Guid.CreateVersion7());

    await Assert.That(async () => await _dispatcher(provider).LocalInvokeAsync<PbUnhandledResult>(
        (object)unhandled, new DispatchOptions().WithBehaviors(DispatchBehaviors.ContinueWhenNoHandler)))
      .ThrowsExactly<ReceptorNotFoundException>()
      .Because("there is no result to return without a handler; the policy refuses rather than invent one");
  }

  [Test]
  public async Task LocalInvokeVoidWithOptions_ContinueWhenNoHandler_ReturnsQuietlyAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var unhandled = new PbUnhandledCommand(Guid.CreateVersion7());

    await _dispatcher(provider).LocalInvokeAsync(
      (object)unhandled, new DispatchOptions().WithBehaviors(DispatchBehaviors.ContinueWhenNoHandler));
  }

  // ── E28: the caller's explicit context reaches the receptor on a send as on a local invoke ───

  [Test]
  public async Task SendWithContext_TheReceptorSeesTheCallersCorrelationAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExContextProbeCommand(Guid.CreateVersion7());
    var correlation = CorrelationId.New();

    await _dispatcher(provider).SendAsync(command, MessageContext.Create(correlation));

    await Assert.That(ExContextProbeCommandReceptor.SeenCorrelation(command.Id)).IsEqualTo(correlation.Value);
  }

  [Test]
  public async Task LocalInvokeWithContext_TheReceptorSeesTheCallersCorrelationAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExContextProbeCommand(Guid.CreateVersion7());
    var correlation = CorrelationId.New();

    await _dispatcher(provider).LocalInvokeAsync((object)command, MessageContext.Create(correlation));

    await Assert.That(ExContextProbeCommandReceptor.SeenCorrelation(command.Id)).IsEqualTo(correlation.Value);
  }

  // ── helpers ──────────────────────────────────────────────────────────────────────────────────

  private static IDispatcher _dispatcher(ServiceProvider provider) => provider.GetRequiredService<IDispatcher>();

  private static MessageContext _context() => MessageContext.Create(CorrelationId.New());

  private static async Task _assertRpcAsync(ExRpcResponse? response, Guid id) {
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.Id).IsEqualTo(id);
    await Assert.That(PolicyProbe.Count(nameof(ExRpcCommandReceptor), id)).IsEqualTo(1)
      .Because("the receptor runs once");
    await Assert.That(PolicyProbe.Count(nameof(ExFollowUpEventReceptor), id)).IsEqualTo(1)
      .Because("the rest of the tuple cascades and runs here, whichever overload asked for the response");
  }

  private static async Task _assertRanHereAndNeverTransportedAsync(PolicyRecordingOutbox outbox, Guid id) {
    await Assert.That(PolicyProbe.Count(nameof(ExFollowUpEventReceptor), id)).IsEqualTo(1)
      .Because("DefaultRouting(Local) runs the result here");
    var queued = outbox.QueuedOf(typeof(ExFollowUpEvent));
    await Assert.That(queued).IsNotEmpty().Because("Local persists the result");
    await Assert.That(queued.All(m => m.Destination is null)).IsTrue()
      .Because("Local never transports the result");
  }

  private static async Task _invokeAtStageAsync<TMessage>(ServiceProvider provider, TMessage message, LifecycleStage stage)
      where TMessage : notnull {
    await using var scope = provider.CreateAsyncScope();
    var invoker = scope.ServiceProvider.GetRequiredService<IReceptorInvoker>();
    var envelope = new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Inbox }
    };
    await invoker.InvokeAsync(envelope, stage, new LifecycleExecutionContext { CurrentStage = stage, MessageSource = MessageSource.Inbox });
  }

  private static ServiceProvider _buildProvider(IWorkCoordinatorStrategy outbox, Action<IServiceCollection>? configure = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton<IEnvelopeSerializer, PolicyContextKeepingEnvelopeSerializer>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN, "Whizbang.Core.Component.Tests.PolicyBehavior.Owned"));
    configure?.Invoke(services);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }

  private sealed class ClosedSchemaGate : ISchemaReadyGate {
    public bool IsReady => false;
    public Task WaitForReadyAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
    public void MarkReady() => throw new NotSupportedException("this gate stays closed for the test");
  }
}
