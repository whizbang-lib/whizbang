// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.PolicyBehavior;
using Whizbang.Core.Component.Tests.PolicyBehavior.Owned;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Dispatch.Policy;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// The dispatch policy's decisions carried out by the REAL generated dispatcher (#1395): what runs
/// here, what is queued, and how the queued copy is marked and scheduled. One test per defect the
/// central policy fixes.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision-table</docs>
public class DispatchPolicyBehaviorComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.PolicyBehavior.Owned";
  private static readonly DateTimeOffset _future = DateTimeOffset.UtcNow.AddYears(10);

  [Test]
  public async Task Send_UnownedHandlerThrows_StillQueuesTheCopyAndSurfacesTheExceptionAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbThrowingUnownedCommand(Guid.CreateVersion7());

    await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().SendAsync(command))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("pb-boom");

    await Assert.That(PolicyProbe.Count(nameof(PbThrowingUnownedCommandReceptor), command.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(PbThrowingUnownedCommand))).Count().IsEqualTo(1)
      .Because("the outbox copy is written regardless of the local outcome; other services still get it");
  }

  [Test]
  public async Task Send_ScheduledInTheFuture_IsNotHandledNowAndQueuesAScheduledDeferredCopyAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbUnownedCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().SendAsync(command, new DispatchOptions().WithScheduledFor(_future));

    await Assert.That(PolicyProbe.Count(nameof(PbUnownedCommandReceptor), command.Id)).IsEqualTo(0)
      .Because("a scheduled send waits for its time (#1397)");
    var queued = outbox.QueuedOf(typeof(PbUnownedCommand));
    await Assert.That(queued).Count().IsEqualTo(1);
    await Assert.That(queued[0].ScheduledFor).IsEqualTo(_future);
    await Assert.That(queued[0].Envelope.DispatchContext.LocalDeferred).IsTrue()
      .Because("this service handles it when the scheduled copy comes back");
    await Assert.That(queued[0].Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsFalse();
  }

  [Test]
  public async Task Publish_HandledHere_MarksTheCopyAsHandledHereAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var evt = new PbEvent(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().PublishAsync(evt);

    await Assert.That(PolicyProbe.Count(nameof(PbEventReceptor), evt.Id)).IsEqualTo(1);
    var queued = outbox.QueuedOf(typeof(PbEvent));
    await Assert.That(queued).Count().IsEqualTo(1);
    await Assert.That(queued[0].Envelope.DispatchContext.Mode).IsEqualTo(DispatchModes.Both)
      .Because("the copy of an event this service handled says so (#1398)");
  }

  [Test]
  public async Task Publish_ScheduledInTheFuture_IsNotHandledNowAndMarksTheCopyDeferredAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var evt = new PbEvent(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().PublishAsync(evt, new DispatchOptions().WithScheduledFor(_future));

    await Assert.That(PolicyProbe.Count(nameof(PbEventReceptor), evt.Id)).IsEqualTo(0);
    var queued = outbox.QueuedOf(typeof(PbEvent));
    await Assert.That(queued).Count().IsEqualTo(1);
    await Assert.That(queued[0].ScheduledFor).IsEqualTo(_future);
    await Assert.That(queued[0].Envelope.DispatchContext.LocalDeferred).IsTrue()
      .Because("the publishing service handles its own scheduled event when it comes back (#1401)");
  }

  [Test]
  public async Task Publish_RuntimeReceptorAtALocalStage_RunsAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    provider.GetRequiredService<IReceptorRegistry>().Register(new PbRuntimeReceptor<PbRuntimeEvent>(), LifecycleStage.LocalImmediateInline);
    var evt = new PbRuntimeEvent(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().PublishAsync(evt);

    await Assert.That(PolicyProbe.Count("PbRuntimeReceptor", evt.Id)).IsEqualTo(1)
      .Because("a receptor registered at runtime at a local stage runs when the event is published (#1399)");
  }

  [Test]
  public async Task Publish_ReceptorDeclaredAtALocalStage_RunsAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var evt = new PbHookedEvent(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().PublishAsync(evt);

    await Assert.That(PolicyProbe.Count(nameof(PbLocalStageHook), evt.Id)).IsEqualTo(1)
      .Because("a receptor declared [FireAt(LocalImmediateInline)] runs when this service handles the event (#1400)");
  }

  [Test]
  public async Task Publish_ReceptorDeclaredAtALocalStage_RunsExactlyOnceAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var evt = new PbHookedEvent(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().PublishAsync(evt);

    await Assert.That(PolicyProbe.Count(nameof(PbLocalStageHook), evt.Id)).IsEqualTo(1)
      .Because("the publish fan-out runs it; nothing else may run it a second time");
  }

  [Test]
  public async Task PublishMany_VoidOnlyReceptor_RunsLocallyAndQueuesAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var evt = new PbVoidOnlyEvent(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().PublishManyAsync([evt]);

    await Assert.That(PolicyProbe.Count(nameof(PbVoidOnlyEventReceptor), evt.Id)).IsEqualTo(1)
      .Because("PublishManyAsync runs a void receptor like PublishAsync does (#1402)");
    await Assert.That(outbox.QueuedOf(typeof(PbVoidOnlyEvent))).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Cascade_OwnedCommandWithTheDefaultRoute_StaysInTheServiceAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var trigger = new PbCascadeTrigger(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync(trigger);

    await Assert.That(PolicyProbe.Count(nameof(PbOwnedCommandReceptor), trigger.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(PbOwnedCommand))).IsEmpty()
      .Because("an owned command never leaves its service, whatever the cascade's route (#1403)");
  }

  [Test]
  public async Task LocalInvokeTyped_ExtractedCascade_RunsLocallyAndGoesOutAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var request = new PbRpcRequest(Guid.CreateVersion7());

    var response = await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync<PbRpcResponse>(request);

    await Assert.That(response.Id).IsEqualTo(request.Id);
    await Assert.That(PolicyProbe.Count(nameof(PbEventReceptor), request.Id)).IsEqualTo(1)
      .Because("the cascaded event's default route includes local dispatch (#1404)");
    var queued = outbox.QueuedOf(typeof(PbEvent));
    await Assert.That(queued).Count().IsEqualTo(1);
    await Assert.That(queued[0].Envelope.DispatchContext.Mode).IsEqualTo(DispatchModes.Both);
  }

  [Test]
  public async Task Cascade_CommandRoutedToTheEventStoreOnly_ThrowsAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var trigger = new PbStoreOnlyTrigger(Guid.CreateVersion7());

    var exception = await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().SendAsync(trigger))
      .ThrowsExactly<DispatchRejectedException>();

    await Assert.That(exception!.Rejection).IsEqualTo(DispatchRejection.CommandCannotBeStoreOnly);
    await Assert.That(PolicyProbe.Count(nameof(PbUnownedCommandReceptor), trigger.Id)).IsEqualTo(0);
  }

  [Test]
  public async Task LocalInvoke_ScheduledInTheFuture_ThrowsAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbUnownedCommand(Guid.CreateVersion7());

    var exception = await Assert.That(async () =>
        await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync((object)command, new DispatchOptions().WithScheduledFor(_future)))
      .ThrowsExactly<DispatchRejectedException>();

    await Assert.That(exception!.Rejection).IsEqualTo(DispatchRejection.CannotScheduleInProcess);
    await Assert.That(PolicyProbe.Count(nameof(PbUnownedCommandReceptor), command.Id)).IsEqualTo(0);
  }

  [Test]
  public async Task Send_TimeoutElapsesDuringTheOutboxFlush_CancelsTheDispatchAsync() {
    var outbox = new BlockingFlushOutbox();
    var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
    await using var provider = _buildProvider(outbox, clock);
    var command = new PbUnownedCommand(Guid.CreateVersion7());

    var send = provider.GetRequiredService<IDispatcher>().SendAsync(command, new DispatchOptions { Timeout = TimeSpan.FromSeconds(5) });
    await outbox.FlushStarted.Task;
    clock.Advance(TimeSpan.FromSeconds(6));

    await Assert.That(async () => await send).Throws<OperationCanceledException>()
      .Because("DispatchOptions.Timeout cancels the dispatch on the dispatcher's clock, flush included");
    await Assert.That(PolicyProbe.Count(nameof(PbUnownedCommandReceptor), command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task Send_CallerCancelsDuringTheOutboxFlush_CancelsTheDispatchAsync() {
    var outbox = new BlockingFlushOutbox();
    await using var provider = _buildProvider(outbox);
    using var cancellation = new CancellationTokenSource();
    var command = new PbUnownedCommand(Guid.CreateVersion7());

    var send = provider.GetRequiredService<IDispatcher>().SendAsync(command, new DispatchOptions().WithCancellationToken(cancellation.Token));
    await outbox.FlushStarted.Task;
    await cancellation.CancelAsync();

    await Assert.That(async () => await send).Throws<OperationCanceledException>();
  }

  private static ServiceProvider _buildProvider(IWorkCoordinatorStrategy outbox, TimeProvider? clock = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    if (clock is not null) {
      services.AddSingleton(clock);
    }
    services.AddSingleton<IEnvelopeSerializer, PolicyContextKeepingEnvelopeSerializer>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
