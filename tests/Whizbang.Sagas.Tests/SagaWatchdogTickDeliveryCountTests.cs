using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Sagas.Tests.Generators;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// A <c>[Saga]</c>-declared saga's generated tick receiver, which declares no <c>[FireAt]</c>, handles
/// each watchdog tick once, whichever path the tick takes after it is published.
/// </summary>
/// <remarks>
/// <para>
/// Every tick handling re-arms, stalls or abandons the saga, so a second handling of one tick doubles
/// the stall count and brings the saga to abandonment in half its configured stalls. The count here
/// is of handlings: a receiver that finds the saga still running re-arms it once per handling, so the
/// re-armed ticks in the outbox are the handlings, one for one.
/// </para>
/// <para>
/// A tick published without a schedule, as the stranded-saga sweep publishes one, is handled at once
/// by the publish's local path. Its stored row then goes through every stage the outbox and the inbox
/// run for it, received by its own service or by another host of the same saga.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
[Category("Integration")]
[Category("Saga")]
public class SagaWatchdogTickDeliveryCountTests {

  private static readonly LifecycleStage[] _stagesAfterTheOutbox = [
    LifecycleStage.PreOutboxDetached, LifecycleStage.PreOutboxInline,
    LifecycleStage.PostOutboxDetached, LifecycleStage.PostOutboxInline,
  ];

  private static readonly LifecycleStage[] _stagesOfTheInbox = [
    LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
    LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline,
  ];

  private static SagaCompletionWatchdogTickEvent _tick() => new() {
    StreamId = Guid.CreateVersion7(),
    SagaName = GeneratorTestDefaultSaga.SagaName,
    EntityId = Guid.CreateVersion7(),
  };

  /// <summary>How many times each host's generated receiver handled the tick.</summary>
  private static int _handlings(CapturedOutboxHost host, SagaCompletionWatchdogTickEvent tick) =>
    host.Outbox.Rows.Count(r => r.Payload is SagaCompletionWatchdogTickEvent rearmed
                                && rearmed.StreamId == tick.StreamId
                                && rearmed.RescheduleCount == tick.RescheduleCount + 1);

  private static async Task _runStagesAsync(CapturedOutboxHost receiver, MessageEnvelope<SagaCompletionWatchdogTickEvent> envelope, IEnumerable<LifecycleStage> stages) {
    await using var scope = receiver.Provider.CreateAsyncScope();
    var invoker = new ReceptorInvoker(receiver.Provider.GetRequiredService<IReceptorRegistry>(), scope.ServiceProvider);
    foreach (var stage in stages) {
      await invoker.InvokeAsync(envelope, stage);
    }
  }

  private static CapturedOutboxHost.StoredRow _row(CapturedOutboxHost host, SagaCompletionWatchdogTickEvent tick) =>
    host.Outbox.Rows.Single(r => ReferenceEquals(r.Payload, tick));

  /// <summary>The tick published and received by the saga's own service is handled once.</summary>
  [Test]
  public async Task ImmediateTick_ReceivedByItsOwnService_IsHandledOnceAsync() {
    await using var host = CapturedOutboxHost.Create();
    var tick = _tick();

    await host.Dispatcher.PublishAsync(tick);
    var envelope = _row(host, tick).Rehydrate<SagaCompletionWatchdogTickEvent>();
    await _runStagesAsync(host, envelope, [.. _stagesAfterTheOutbox, .. _stagesOfTheInbox]);

    await Assert.That(_handlings(host, tick)).IsEqualTo(1)
      .Because("a tick handled twice counts two stalls for one check");
  }

  /// <summary>
  /// The tick received by another host of the same saga is not handled a second time there: it was
  /// handled when it was published.
  /// </summary>
  /// <remarks>
  /// Watchdog ticks share one topic, so every host that declares a saga of the same name receives
  /// every tick for it. The receiving host is a different service, so the rule that skips the inbox
  /// stages for a message from this same service does not apply. What stops the second handling is the
  /// receptor invocation record the publishing host's local path wrote on the envelope before the
  /// envelope was stored.
  /// </remarks>
  [Test]
  public async Task ImmediateTick_ReceivedByAnotherHostOfTheSameSaga_IsNotHandledAgainAsync() {
    await using var publisher = CapturedOutboxHost.Create();
    await using var otherHost = CapturedOutboxHost.Create(services =>
      services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.CreateVersion7(), "other-host", "host-b", 2)));
    var tick = _tick();

    await publisher.Dispatcher.PublishAsync(tick);
    var envelope = _row(publisher, tick).Rehydrate<SagaCompletionWatchdogTickEvent>();
    await _runStagesAsync(publisher, envelope, _stagesAfterTheOutbox);
    await _runStagesAsync(otherHost, envelope, _stagesOfTheInbox);

    await Assert.That(_handlings(publisher, tick) + _handlings(otherHost, tick)).IsEqualTo(1)
      .Because("one tick is one check of the saga, wherever it is delivered");
  }

  /// <summary>
  /// The record written for the local path names the receiver and the stage it ran at, so an operator
  /// reading the stored envelope sees where the tick was handled.
  /// </summary>
  [Test]
  public async Task ImmediateTick_StoredEnvelope_RecordsTheLocalHandlingAsync() {
    await using var host = CapturedOutboxHost.Create();
    var tick = _tick();

    await host.Dispatcher.PublishAsync(tick);

    var record = _row(host, tick).ReceptorInvocations
      .Single(r => r.ReceptorId.EndsWith("GeneratorTestDefaultSaga.SagaCompletionWatchdogTickHandler", StringComparison.Ordinal));
    await Assert.That(record.Stage).IsEqualTo(LifecycleStage.LocalImmediateInline);
  }

  /// <summary>
  /// A scheduled tick is not handled when it is published, so nothing is recorded for it and the
  /// receiving host handles it once.
  /// </summary>
  [Test]
  public async Task ScheduledTick_ReceivedByAnotherHostOfTheSameSaga_IsHandledOnceThereAsync() {
    await using var publisher = CapturedOutboxHost.Create();
    await using var otherHost = CapturedOutboxHost.Create(services =>
      services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.CreateVersion7(), "other-host", "host-b", 2)));
    var tick = _tick();

    await new Whizbang.Sagas.Services.DispatcherSagaEventEmitter(publisher.Dispatcher).PublishAsync(tick, DateTimeOffset.UtcNow.AddMinutes(5));
    var envelope = _row(publisher, tick).Rehydrate<SagaCompletionWatchdogTickEvent>();
    await _runStagesAsync(publisher, envelope, _stagesAfterTheOutbox);
    await _runStagesAsync(otherHost, envelope, _stagesOfTheInbox);

    await Assert.That(_handlings(publisher, tick)).IsEqualTo(0)
      .Because("a scheduled tick waits for its time; handling it at publish would re-arm at once and cascade");
    await Assert.That(_handlings(otherHost, tick)).IsEqualTo(1);
  }
}
