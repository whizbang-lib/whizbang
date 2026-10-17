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
/// The dispatch policy's failure and override paths on the REAL generated dispatcher (#1395):
/// failures on both the local run and the outbox write, batch failure modes, overrides, refusals
/// only a hook or a selected policy can produce, and the paths a host's registrations change.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision</docs>
public class DispatchPolicyEdgeComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.PolicyBehavior.Owned";
  private static readonly DateTimeOffset _future = DateTimeOffset.UtcNow.AddYears(10);

  // Local and outbox both fail

  [Test]
  public async Task Send_HandlerAndOutboxBothFail_SurfacesBothAsync() {
    await using var provider = _buildProvider(new ThrowingOutbox());
    var command = new PbThrowingUnownedCommand(Guid.CreateVersion7());

    var exception = await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().SendAsync(command))
      .ThrowsExactly<AggregateException>();

    await Assert.That(exception!.InnerExceptions).Count().IsEqualTo(2);
    await Assert.That(exception.InnerExceptions[0].Message).IsEqualTo("pb-boom");
    await Assert.That(exception.InnerExceptions[1].Message).IsEqualTo("outbox-boom");
  }

  [Test]
  public async Task Cascade_LocalReceptorThrows_StillWritesTheCopyAndSurfacesTheFailureAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var trigger = new PbThrowingCascadeTrigger(Guid.CreateVersion7());

    await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync(trigger))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("pb-event-boom");

    await Assert.That(outbox.QueuedOf(typeof(PbThrowingEvent))).Count().IsEqualTo(1)
      .Because("a cascaded copy is written whatever the local outcome");
  }

  [Test]
  public async Task Cascade_LocalReceptorAndOutboxBothFail_SurfacesBothAsync() {
    await using var provider = _buildProvider(new ThrowingOutbox());
    var trigger = new PbThrowingCascadeTrigger(Guid.CreateVersion7());

    var exception = await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync(trigger))
      .ThrowsExactly<AggregateException>();

    await Assert.That(exception!.InnerExceptions).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Publish_LocalReceptorAndOutboxBothFail_SurfacesTheLocalFailureAsync() {
    await using var provider = _buildProvider(new ThrowingOutbox());

    await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().PublishAsync(new PbThrowingEvent(Guid.CreateVersion7())))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("pb-event-boom");
  }

  [Test]
  public async Task Publish_OwnedCommand_IsHandledHereAndNeverQueuedAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbOwnedCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().PublishAsync(command);

    await Assert.That(PolicyProbe.Count(nameof(PbOwnedCommandReceptor), command.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(PbOwnedCommand))).IsEmpty()
      .Because("an owned command never leaves its service, even when it is published (H)");
  }

  // Batches

  [Test]
  public async Task SendMany_StopMode_StopsAtTheFailureAndQueuesItsCopyAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var failing = new PbThrowingUnownedCommand(Guid.CreateVersion7());
    var after = new PbUnownedCommand(Guid.CreateVersion7());

    await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().SendManyAsync([(object)failing, after]))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("pb-boom");

    await Assert.That(PolicyProbe.Count(nameof(PbUnownedCommandReceptor), after.Id)).IsEqualTo(0)
      .Because("Stop leaves the messages after the failure neither handled nor queued");
    await Assert.That(outbox.QueuedOf(typeof(PbThrowingUnownedCommand))).Count().IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(PbUnownedCommand))).IsEmpty();
  }

  [Test]
  public async Task SendMany_ContinueMode_HandlesEveryMessageThenAggregatesAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var failing = new PbThrowingUnownedCommand(Guid.CreateVersion7());
    var after = new PbUnownedCommand(Guid.CreateVersion7());
    var options = new DispatchOptions().WithBatchFailure(BatchFailureMode.ContinueAndAggregate);

    var exception = await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().SendManyAsync([(object)failing, after], options))
      .ThrowsExactly<AggregateException>();

    await Assert.That(exception!.InnerExceptions).Count().IsEqualTo(1);
    await Assert.That(PolicyProbe.Count(nameof(PbUnownedCommandReceptor), after.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(PbUnownedCommand))).Count().IsEqualTo(1);
  }

  [Test]
  public async Task SendMany_LocalAndOutboxBothFail_SurfacesBothAsync() {
    await using var provider = _buildProvider(new ThrowingOutbox());
    var failing = new PbThrowingUnownedCommand(Guid.CreateVersion7());

    var exception = await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().SendManyAsync([(object)failing]))
      .ThrowsExactly<AggregateException>();

    await Assert.That(exception!.InnerExceptions).Count().IsEqualTo(2);
  }

  // Overrides and refusals

  [Test]
  public async Task Send_OwnedCommandWithNoHandler_ContinueOverride_ReturnsQuietlyAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var command = new PbOwnedUnhandledCommand(Guid.CreateVersion7());

    var receipt = await provider.GetRequiredService<IDispatcher>().SendAsync(command,
      new DispatchOptions().WithBehaviors(DispatchBehaviors.ContinueWhenNoHandler));

    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(outbox.QueuedOf(typeof(PbOwnedUnhandledCommand))).IsEmpty();
  }

  [Test]
  public async Task LocalInvokeVoid_NoHandler_ContinueOverride_ReturnsQuietlyAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());

    await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync(
        (object)new PbUnhandledCommand(Guid.CreateVersion7()), new DispatchOptions().WithBehaviors(DispatchBehaviors.ContinueWhenNoHandler)))
      .ThrowsNothing();
  }

  [Test]
  public async Task Send_ContradictoryOverrides_ThrowsArgumentExceptionAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var options = new DispatchOptions().WithBehaviors(DispatchBehaviors.ThrowWhenNoHandler | DispatchBehaviors.ContinueWhenNoHandler);

    await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().SendAsync(new PbUnownedCommand(Guid.CreateVersion7()), options))
      .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task LocalInvokeTyped_SelectedPolicyDefersTheHandler_ThrowsResultUnavailableAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var options = new DispatchOptions().WithPolicy(DispatchPolicies.Send.Id).WithScheduledFor(_future);

    var exception = await Assert.That(async () => await provider.GetRequiredService<IDispatcher>()
        .LocalInvokeAsync<PbRpcResponse>((object)new PbRpcRequest(Guid.CreateVersion7()), options))
      .ThrowsExactly<DispatchRejectedException>();

    await Assert.That(exception!.Rejection).IsEqualTo(DispatchRejection.ResultUnavailable);
  }

  [Test]
  public async Task Send_HookRefusesWithNoRoute_ThrowsArgumentExceptionAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox(),
      configure: services => services.AddSingleton<IDispatchPolicyHook>(new RefusingPolicyHook(DispatchRejection.NoRoute)));

    await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().SendAsync(new PbUnownedCommand(Guid.CreateVersion7())))
      .ThrowsExactly<ArgumentException>()
      .Because("a registered hook is applied, and its refusal surfaces as the exception that cause has always raised");
  }

  [Test]
  public async Task Send_CustomRegistry_IsUsedToResolveASelectedPolicyAsync() {
    var registry = new DispatchPolicyRegistry();
    var keepLocal = new DispatchPolicy(new DispatchPolicyId(1100), "KeepLocal", DispatchExpectations.LocalOnly, NoOutboxBehavior.Throw,
      DispatchRules.LocalCallNeverSendsOut);
    registry.Register(keepLocal);
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox, configure: services => services.AddSingleton<IDispatchPolicyRegistry>(registry));
    var command = new PbUnownedCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().SendAsync(command, new DispatchOptions().WithPolicy(keepLocal.Id));

    await Assert.That(PolicyProbe.Count(nameof(PbUnownedCommandReceptor), command.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(PbUnownedCommand))).IsEmpty()
      .Because("the selected custom policy keeps the command local");
  }

  // Missing handlers on the typed local paths

  [Test]
  public async Task LocalInvokeTypedWithOptions_NoHandler_ThrowsReceptorNotFoundAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());

    await Assert.That(async () => await provider.GetRequiredService<IDispatcher>()
        .LocalInvokeAsync<PbUnhandledResult>((object)new PbUnhandledCommand(Guid.CreateVersion7()), new DispatchOptions()))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task LocalInvokeWithReceipt_NoHandler_ThrowsReceptorNotFoundAsync(bool withOptions) {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var command = new PbUnhandledCommand(Guid.CreateVersion7());

    await Assert.That(async () => {
      if (withOptions) {
        await dispatcher.LocalInvokeWithReceiptAsync<PbUnhandledResult>((object)command, new DispatchOptions());
      } else {
        await dispatcher.LocalInvokeWithReceiptAsync<PbUnhandledResult>((object)command);
      }
    }).ThrowsExactly<ReceptorNotFoundException>();
  }

  // Sync receptors on the full path

  [Test]
  public async Task LocalInvokeVoidWithOptions_SyncReceptorWithATraceStore_RunsOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox(),
      configure: services => services.AddSingleton<ITraceStore, InMemoryTraceStore>());
    var command = new PbSyncCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync((object)command, new DispatchOptions());

    await Assert.That(PolicyProbe.Count(nameof(PbSyncCommandReceptor), command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeVoidWithOptions_SyncReceptorWithAPostLifecycleHook_RunsBothAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new PbSyncCommandWithLifecycle(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync((object)command, new DispatchOptions());

    await Assert.That(PolicyProbe.Count(nameof(PbSyncCommandWithLifecycleReceptor), command.Id)).IsEqualTo(1);
    await Assert.That(PolicyProbe.Count(nameof(PbSyncCommandWithLifecycleHook), command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeVoidWithOptions_PlainSyncReceptor_RunsOnceAsync() {
    await using var provider = _buildProvider(new PolicyRecordingOutbox());
    var command = new PbSyncCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync((object)command, new DispatchOptions());

    await Assert.That(PolicyProbe.Count(nameof(PbSyncCommandReceptor), command.Id)).IsEqualTo(1)
      .Because("with no trace store and nothing after the handler, the sync receptor takes the direct path");
  }

  [Test]
  public async Task Publish_CommandNothingHereHandles_GoesOutWithoutALocalRunAsync() {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);

    await provider.GetRequiredService<IDispatcher>().PublishAsync(new PbUnhandledCommand(Guid.CreateVersion7()));

    await Assert.That(outbox.QueuedOf(typeof(PbUnhandledCommand))).Count().IsEqualTo(1);
  }

  // Registrations that resolve to nothing

  [Test]
  public async Task Send_HandledHereWithAWorkCoordinatorResolvingToNothing_ReturnsTheLocalReceiptAsync() {
    var services = _baseServices();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => null!);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    await using var provider = services.BuildServiceProvider();
    var command = new PbUnownedCommand(Guid.CreateVersion7());

    var receipt = await provider.GetRequiredService<IDispatcher>().SendAsync(command);

    await Assert.That(PolicyProbe.Count(nameof(PbUnownedCommandReceptor), command.Id)).IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered)
      .Because("handled here with nowhere to send it, the local run is the delivery");
  }

  [Test]
  public async Task Send_WorkCoordinatorRegisteredButResolvingToNothing_ThrowsReceptorNotFoundAsync() {
    var services = _baseServices();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => null!);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(async () => await provider.GetRequiredService<IDispatcher>().SendAsync(new PbUnhandledCommand(Guid.CreateVersion7())))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  private static ServiceCollection _baseServices() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IEnvelopeSerializer, PolicyContextKeepingEnvelopeSerializer>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    return services;
  }

  private static ServiceProvider _buildProvider(IWorkCoordinatorStrategy outbox, Action<IServiceCollection>? configure = null) {
    var services = _baseServices();
    services.AddScoped(_ => outbox);
    configure?.Invoke(services);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
