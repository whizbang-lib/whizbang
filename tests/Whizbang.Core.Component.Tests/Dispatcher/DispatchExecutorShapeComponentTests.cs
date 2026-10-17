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

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Every handler shape the generator recognizes runs, exactly as often as the dispatch decision says,
/// on every executor that can reach it (#1410 follow-up). Each test runs the REAL generated dispatcher
/// and registry and asserts what actually ran: a decision alone proves nothing about execution.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision-table</docs>
public class DispatchExecutorShapeComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.ExecutorShapes.Owned";

  // ── E1: an event whose receptor returns a result ─────────────────────────────────────────────

  [Test]
  public async Task Publish_EventWithAResultReceptor_RunsItOnceAndCascadesTheResultAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var evt = new ExTypedEvent(Guid.CreateVersion7());

    await _dispatcher(provider).PublishAsync(evt);

    await _assertRanOnceAndCascadedAsync(nameof(ExTypedEventReceptor), evt.Id);
  }

  [Test]
  public async Task PublishMany_EventWithAResultReceptor_RunsItOnceAndCascadesTheResultAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var evt = new ExTypedEvent(Guid.CreateVersion7());

    await _dispatcher(provider).PublishManyAsync(new object[] { evt });

    await _assertRanOnceAndCascadedAsync(nameof(ExTypedEventReceptor), evt.Id);
  }

  [Test]
  [Arguments(DispatchModes.Local)]
  [Arguments(DispatchModes.Both)]
  public async Task Cascade_EventWithAResultReceptor_RunsItOnceAndCascadesTheResultAsync(DispatchModes mode) {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var evt = new ExTypedEvent(Guid.CreateVersion7());

    await _dispatcher(provider).CascadeMessageAsync(evt, sourceEnvelope: null, mode);

    await _assertRanOnceAndCascadedAsync(nameof(ExTypedEventReceptor), evt.Id);
  }

  [Test]
  public async Task PostInbox_EventWithAResultReceptorFromAnotherService_RunsItOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var evt = new ExTypedEvent(Guid.CreateVersion7());

    await _invokeAtStageAsync(provider, evt, LifecycleStage.PostInboxDetached, MessageSource.Inbox);

    await Assert.That(PolicyProbe.Count(nameof(ExTypedEventReceptor), evt.Id)).IsEqualTo(1)
      .Because("a copy from another service runs the receptor through the registry");
  }

  // ── E1b: a typed event receptor declared at a pipeline stage runs at that stage only ─────────

  [Test]
  public async Task Publish_PipelineStageTypedEventReceptor_DoesNotRunOnPublishAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var evt = new ExPipelineObjectEvent(Guid.CreateVersion7());

    await _dispatcher(provider).PublishAsync(evt);

    await Assert.That(PolicyProbe.Count(nameof(ExPipelineObjectEventReceptor), evt.Id)).IsEqualTo(0)
      .Because("a receptor declared at a pipeline stage fires at that stage, not on the local publish");
  }

  [Test]
  public async Task PostInbox_PipelineStageTypedEventReceptor_RunsOnceAtItsStageAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var evt = new ExPipelineObjectEvent(Guid.CreateVersion7());

    await _invokeAtStageAsync(provider, evt, LifecycleStage.PostInboxInline, MessageSource.Inbox);

    await Assert.That(PolicyProbe.Count(nameof(ExPipelineObjectEventReceptor), evt.Id)).IsEqualTo(1);
  }

  // ── E2: sync receptors on publish and cascade ────────────────────────────────────────────────

  [Test]
  public async Task Publish_EventWithAVoidSyncReceptor_RunsItOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var evt = new ExVoidSyncEvent(Guid.CreateVersion7());

    await _dispatcher(provider).PublishAsync(evt);

    await Assert.That(PolicyProbe.Count(nameof(ExVoidSyncEventReceptor), evt.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task Publish_EventWithAResultSyncReceptor_RunsItOnceAndCascadesTheResultAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var evt = new ExResultSyncEvent(Guid.CreateVersion7());

    await _dispatcher(provider).PublishAsync(evt);

    await _assertRanOnceAndCascadedAsync(nameof(ExResultSyncEventReceptor), evt.Id);
  }

  [Test]
  public async Task Cascade_EventWithAResultSyncReceptor_RunsItOnceAndCascadesTheResultAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var evt = new ExResultSyncEvent(Guid.CreateVersion7());

    await _dispatcher(provider).CascadeMessageAsync(evt, sourceEnvelope: null, DispatchModes.Both);

    await _assertRanOnceAndCascadedAsync(nameof(ExResultSyncEventReceptor), evt.Id);
  }

  [Test]
  public async Task Publish_OwnedCommandWithOnlyAVoidSyncReceptor_RunsItOnceAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new ExOwnedVoidSyncCommand(Guid.CreateVersion7());

    await _dispatcher(provider).PublishAsync(command);

    await Assert.That(PolicyProbe.Count(nameof(ExOwnedVoidSyncCommandReceptor), command.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(ExOwnedVoidSyncCommand))).IsEmpty();
  }

  [Test]
  public async Task Cascade_OwnedCommandWithOnlyAVoidSyncReceptor_RunsItOnceAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new ExOwnedVoidSyncCommand(Guid.CreateVersion7());

    await _dispatcher(provider).CascadeMessageAsync(command, sourceEnvelope: null, DispatchModes.Outbox);

    await Assert.That(PolicyProbe.Count(nameof(ExOwnedVoidSyncCommandReceptor), command.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(ExOwnedVoidSyncCommand))).IsEmpty();
  }

  // ── E3: sync receptors at a worker stage ─────────────────────────────────────────────────────

  [Test]
  public async Task PostInbox_EventWithAVoidSyncReceptor_RunsItOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var evt = new ExVoidSyncEvent(Guid.CreateVersion7());

    await _invokeAtStageAsync(provider, evt, LifecycleStage.PostInboxDetached, MessageSource.Inbox);

    await Assert.That(PolicyProbe.Count(nameof(ExVoidSyncEventReceptor), evt.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task PostInbox_EventWithAResultSyncReceptor_RunsItOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var evt = new ExResultSyncEvent(Guid.CreateVersion7());

    await _invokeAtStageAsync(provider, evt, LifecycleStage.PostInboxDetached, MessageSource.Inbox);

    await Assert.That(PolicyProbe.Count(nameof(ExResultSyncEventReceptor), evt.Id)).IsEqualTo(1);
  }

  // ── E4: polymorphic receptors on local paths ─────────────────────────────────────────────────

  [Test]
  public async Task Publish_EventHandledThroughItsInterface_RunsTheReceptorOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var evt = new ExCircleEvent(Guid.CreateVersion7());

    await _dispatcher(provider).PublishAsync(evt);

    await Assert.That(PolicyProbe.Count(nameof(ExShapeEventReceptor), evt.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task Send_CommandHandledThroughItsInterface_RunsTheReceptorOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExSquareCommand(Guid.CreateVersion7());

    await _dispatcher(provider).SendAsync(command);

    await Assert.That(PolicyProbe.Count(nameof(ExShapeCommandReceptor), command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvoke_CommandHandledThroughItsInterface_RunsTheReceptorOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExSquareCommand(Guid.CreateVersion7());

    await _dispatcher(provider).LocalInvokeAsync((object)command);

    await Assert.That(PolicyProbe.Count(nameof(ExShapeCommandReceptor), command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task Cascade_CommandHandledThroughItsInterface_RunsTheReceptorOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExSquareCommand(Guid.CreateVersion7());

    await _dispatcher(provider).CascadeMessageAsync(command, sourceEnvelope: null, DispatchModes.Outbox);

    await Assert.That(PolicyProbe.Count(nameof(ExShapeCommandReceptor), command.Id)).IsEqualTo(1);
  }

  // ── E5: generic overloads called at an interface type use the message's own type ─────────────

  [Test]
  public async Task SendGeneric_AtAnInterfaceType_RunsTheMessagesReceptorOnceAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new ExVoidCommand(Guid.CreateVersion7());

    await _dispatcher(provider).SendAsync<ICommand>(command);

    await Assert.That(PolicyProbe.Count(nameof(ExVoidCommandReceptor), command.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(ExVoidCommand))).IsEmpty()
      .Because("ownership is the message's own namespace, not the declared type's");
  }

  [Test]
  public async Task SendGenericWithOptions_AtAnInterfaceType_RunsTheMessagesReceptorOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExVoidCommand(Guid.CreateVersion7());

    await _dispatcher(provider).SendAsync<ICommand>(command, new DispatchOptions());

    await Assert.That(PolicyProbe.Count(nameof(ExVoidCommandReceptor), command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task SendManyGeneric_AtAnInterfaceType_RunsTheMessagesReceptorOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExVoidCommand(Guid.CreateVersion7());

    await _dispatcher(provider).SendManyAsync<ICommand>([command]);

    await Assert.That(PolicyProbe.Count(nameof(ExVoidCommandReceptor), command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task LocalSendManyGeneric_AtAnInterfaceType_RunsTheMessagesReceptorOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExVoidCommand(Guid.CreateVersion7());

    await _dispatcher(provider).LocalSendManyAsync<ICommand>([command]);

    await Assert.That(PolicyProbe.Count(nameof(ExVoidCommandReceptor), command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task PublishManyGeneric_AtAnInterfaceType_RunsTheMessagesReceptorOnceAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var evt = new ExVoidEvent(Guid.CreateVersion7());

    await _dispatcher(provider).PublishManyAsync<IEvent>([evt]);

    await Assert.That(PolicyProbe.Count(nameof(ExVoidEventReceptor), evt.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(ExVoidEvent))).Count().IsEqualTo(1)
      .Because("the copy is recorded under the event's own type");
  }

  [Test]
  public async Task SendGeneric_ARoutedCommand_HonorsTheRouteAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new ExVoidCommand(Guid.CreateVersion7());

    await _dispatcher(provider).SendAsync(Route.Local(command));

    await Assert.That(PolicyProbe.Count(nameof(ExVoidCommandReceptor), command.Id)).IsEqualTo(1)
      .Because("a Routed<T> passed straight to a send is honored (#1395), on the generic overload too");
  }

  // ── E7b: "has a handler" counts only handlers that run here ──────────────────────────────────

  [Test]
  public async Task Publish_OwnedCommandWhoseOnlyReceptorIsAtAPipelineStage_RefusesLikeSendAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExPipelineOnlyCommand(Guid.CreateVersion7());

    await Assert.That(async () => await _dispatcher(provider).SendAsync(command)).ThrowsExactly<ReceptorNotFoundException>();
    await Assert.That(async () => await _dispatcher(provider).PublishAsync(command)).ThrowsExactly<ReceptorNotFoundException>()
      .Because("an owned command never leaves its service, and nothing here runs it: publishing it must refuse, not drop it");
  }

  [Test]
  public async Task Cascade_OwnedCommandWhoseOnlyReceptorIsAtAPipelineStage_RefusesAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new ExPipelineOnlyCommand(Guid.CreateVersion7());

    await Assert.That(async () => await _dispatcher(provider).CascadeMessageAsync(command, sourceEnvelope: null, DispatchModes.Outbox))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ── E8: a routed result keeps its route ──────────────────────────────────────────────────────

  [Test]
  public async Task Send_ReceptorReturnsRouteLocal_RunsTheResultHereAndNeverQueuesItAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var trigger = new ExRouteLocalTrigger(Guid.CreateVersion7());

    await _dispatcher(provider).SendAsync(trigger);

    await Assert.That(PolicyProbe.Count(nameof(ExFollowUpEventReceptor), trigger.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(ExFollowUpEvent))).IsEmpty()
      .Because("Route.Local keeps the result in this service");
  }

  [Test]
  public async Task Send_ReceptorReturnsRouteOutbox_QueuesTheResultAndDoesNotRunItHereAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var trigger = new ExRouteOutboxTrigger(Guid.CreateVersion7());

    await _dispatcher(provider).SendAsync(trigger);

    await Assert.That(PolicyProbe.Count(nameof(ExFollowUpEventReceptor), trigger.Id)).IsEqualTo(0)
      .Because("Route.Outbox sends the result on without running it here");
    await Assert.That(outbox.QueuedOf(typeof(ExFollowUpEvent))).Count().IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeVoid_ReceptorReturnsRouteLocal_NeverQueuesTheResultAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var trigger = new ExRouteLocalTrigger(Guid.CreateVersion7());

    await _dispatcher(provider).LocalInvokeAsync((object)trigger);

    await Assert.That(PolicyProbe.Count(nameof(ExFollowUpEventReceptor), trigger.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(ExFollowUpEvent))).IsEmpty();
  }

  [Test]
  public async Task LocalInvokeVoid_ReceptorReturnsRouteOutbox_DoesNotRunTheResultHereAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var trigger = new ExRouteOutboxTrigger(Guid.CreateVersion7());

    await _dispatcher(provider).LocalInvokeAsync((object)trigger);

    await Assert.That(PolicyProbe.Count(nameof(ExFollowUpEventReceptor), trigger.Id)).IsEqualTo(0);
    await Assert.That(outbox.QueuedOf(typeof(ExFollowUpEvent))).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PostInbox_ReceptorReturnsRouteLocal_NeverQueuesTheResultAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var trigger = new ExRouteLocalTrigger(Guid.CreateVersion7());

    await _invokeAtStageAsync(provider, trigger, LifecycleStage.PostInboxDetached, MessageSource.Inbox);

    await Assert.That(outbox.QueuedOf(typeof(ExFollowUpEvent))).IsEmpty()
      .Because("the worker stage keeps a routed result's route too");
  }

  // ── E10: a runtime-registered receptor's result cascades on every path ───────────────────────

  [Test]
  public async Task Publish_RuntimeReceptorReturnsAResult_RunsOnceAndCascadesTheResultAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    provider.GetRequiredService<IReceptorRegistry>().Register(new ExRuntimeResultReceptor<ExRuntimeEvent>(), LifecycleStage.LocalImmediateInline);
    var evt = new ExRuntimeEvent(Guid.CreateVersion7());

    await _dispatcher(provider).PublishAsync(evt);

    await _assertRanOnceAndCascadedAsync(nameof(ExRuntimeResultReceptor<ExRuntimeEvent>), evt.Id);
  }

  [Test]
  public async Task Cascade_RuntimeReceptorReturnsAResult_RunsOnceAndCascadesTheResultAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    provider.GetRequiredService<IReceptorRegistry>().Register(new ExRuntimeResultReceptor<ExRuntimeEvent>(), LifecycleStage.LocalImmediateInline);
    var evt = new ExRuntimeEvent(Guid.CreateVersion7());

    await _dispatcher(provider).CascadeMessageAsync(evt, sourceEnvelope: null, DispatchModes.Both);

    await _assertRanOnceAndCascadedAsync(nameof(ExRuntimeResultReceptor<ExRuntimeEvent>), evt.Id);
  }

  [Test]
  public async Task Send_RuntimeReceptorReturnsAResult_RunsOnceAndCascadesTheResultAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    provider.GetRequiredService<IReceptorRegistry>().Register(new ExRuntimeResultReceptor<ExRuntimeCommand>(), LifecycleStage.LocalImmediateInline);
    var command = new ExRuntimeCommand(Guid.CreateVersion7());

    await _dispatcher(provider).SendAsync(command);

    await _assertRanOnceAndCascadedAsync(nameof(ExRuntimeResultReceptor<ExRuntimeCommand>), command.Id);
  }

  [Test]
  public async Task Publish_OwnedCommandWithARuntimeResultReceptor_RunsOnceAndCascadesTheResultAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    provider.GetRequiredService<IReceptorRegistry>().Register(new ExRuntimeResultReceptor<ExRuntimeCommand>(), LifecycleStage.LocalImmediateInline);
    var command = new ExRuntimeCommand(Guid.CreateVersion7());

    await _dispatcher(provider).PublishAsync(command);

    await _assertRanOnceAndCascadedAsync(nameof(ExRuntimeResultReceptor<ExRuntimeCommand>), command.Id);
  }

  // ── helpers ──────────────────────────────────────────────────────────────────────────────────

  private static IDispatcher _dispatcher(ServiceProvider provider) => provider.GetRequiredService<IDispatcher>();

  private static async Task _assertRanOnceAndCascadedAsync(string receptor, Guid id) {
    await Assert.That(PolicyProbe.Count(receptor, id)).IsEqualTo(1)
      .Because("the receptor runs here exactly once");
    await Assert.That(PolicyProbe.Count(nameof(ExFollowUpEventReceptor), id)).IsEqualTo(1)
      .Because("its result cascades and runs here exactly once");
  }

  private static async Task _invokeAtStageAsync<TMessage>(ServiceProvider provider, TMessage message, LifecycleStage stage, MessageSource source)
      where TMessage : notnull {
    await using var scope = provider.CreateAsyncScope();
    var invoker = scope.ServiceProvider.GetRequiredService<IReceptorInvoker>();
    var envelope = new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = source }
    };
    await invoker.InvokeAsync(envelope, stage, new LifecycleExecutionContext { CurrentStage = stage, MessageSource = source });
  }

  private static ServiceProvider _buildProvider(IWorkCoordinatorStrategy outbox) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton<IEnvelopeSerializer, PolicyContextKeepingEnvelopeSerializer>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
