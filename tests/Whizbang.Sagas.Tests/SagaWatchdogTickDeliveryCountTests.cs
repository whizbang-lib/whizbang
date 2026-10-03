using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Services;
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

  private static Task _publishScheduledAsync(CapturedOutboxHost host, SagaCompletionWatchdogTickEvent tick)
    => new Whizbang.Sagas.Services.DispatcherSagaEventEmitter(host.Dispatcher).PublishAsync(tick, DateTimeOffset.UtcNow.AddMinutes(5));

  private static CapturedOutboxHost _otherHost() => CapturedOutboxHost.Create(services =>
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.CreateVersion7(), "other-host", "host-b", 2)));

  /// <summary>
  /// A scheduled tick published and received by the saga's own service is handled once, when its time
  /// comes.
  /// </summary>
  /// <remarks>
  /// The normal case: a saga arms its first tick for later when it starts, and every re-arm is
  /// scheduled. The receiver used to sit at the post-inbox stage, which the invoker skips for a message
  /// whose last hop is this same service, because such a message already ran its receptors when it was
  /// published. A scheduled tick ran none, so every one was stored, committed and handed to nobody; only
  /// the stranded-saga sweep's unscheduled ticks were ever handled.
  /// </remarks>
  [Test]
  public async Task ScheduledTick_ReceivedByItsOwnService_IsHandledOnceAsync() {
    await using var host = CapturedOutboxHost.Create();
    var tick = _tick();

    await _publishScheduledAsync(host, tick);
    var envelope = _row(host, tick).Rehydrate<SagaCompletionWatchdogTickEvent>();
    await _runStagesAsync(host, envelope, _stagesAfterTheOutbox);
    var handledBeforeTheInbox = _handlings(host, tick);
    await _runStagesAsync(host, envelope, _stagesOfTheInbox);

    await Assert.That(handledBeforeTheInbox).IsEqualTo(0)
      .Because("a scheduled tick waits for its time; handling it at publish would re-arm at once and cascade");
    await Assert.That(_handlings(host, tick)).IsEqualTo(1)
      .Because("a tick its own service armed has to reach the saga, or the watchdog chain ends at its first link");
  }

  /// <summary>
  /// An unscheduled tick, as the stranded-saga sweep publishes one, is handled once too: on the
  /// receiving side, like every other tick, and not also at publish.
  /// </summary>
  [Test]
  public async Task ImmediateTick_ReceivedByItsOwnService_IsHandledOnceAsync() {
    await using var host = CapturedOutboxHost.Create();
    var tick = _tick();

    await host.Dispatcher.PublishAsync(tick);
    var handledAtPublish = _handlings(host, tick);
    var envelope = _row(host, tick).Rehydrate<SagaCompletionWatchdogTickEvent>();
    await _runStagesAsync(host, envelope, [.. _stagesAfterTheOutbox, .. _stagesOfTheInbox]);

    await Assert.That(handledAtPublish).IsEqualTo(0);
    await Assert.That(_handlings(host, tick)).IsEqualTo(1)
      .Because("a tick handled twice counts two stalls for one check");
  }

  /// <summary>
  /// The publish writes no invocation record for the receiver, because the receiver no longer runs
  /// there, so the record cannot stop it at the inbox.
  /// </summary>
  /// <remarks>
  /// The dispatcher records the receptors its local path runs on the envelope it stores. A record for
  /// this receiver would skip it at the inbox stage, the one place it now runs.
  /// </remarks>
  [Test]
  public async Task ImmediateTick_StoredEnvelope_RecordsNoLocalHandlingOfTheReceiverAsync() {
    await using var host = CapturedOutboxHost.Create();
    var tick = _tick();

    await host.Dispatcher.PublishAsync(tick);

    await Assert.That(_row(host, tick).ReceptorInvocations
        .Any(r => r.ReceptorId.EndsWith("SagaCompletionWatchdogTickHandler", StringComparison.Ordinal)))
      .IsFalse();
  }

  /// <summary>Each host that receives a tick handles it once, whether it was published there or not.</summary>
  /// <remarks>
  /// Ticks share one topic, so a second service that declares a saga of the same name receives every
  /// tick for it and handles it once, as the hand-written router does. Each service checks its own saga
  /// state on it (#1005).
  /// </remarks>
  /// <param name="scheduled">Whether the tick is scheduled or published for now.</param>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Tick_EachReceivingHost_HandlesItOnceAsync(bool scheduled) {
    await using var publisher = CapturedOutboxHost.Create();
    await using var otherHost = _otherHost();
    var tick = _tick();

    if (scheduled) {
      await _publishScheduledAsync(publisher, tick);
    } else {
      await publisher.Dispatcher.PublishAsync(tick);
    }
    // Each host receives its own copy of the stored row off the transport.
    var row = _row(publisher, tick);
    await _runStagesAsync(publisher, row.Rehydrate<SagaCompletionWatchdogTickEvent>(), [.. _stagesAfterTheOutbox, .. _stagesOfTheInbox]);
    await _runStagesAsync(otherHost, row.Rehydrate<SagaCompletionWatchdogTickEvent>(), _stagesOfTheInbox);

    await Assert.That(_handlings(publisher, tick)).IsEqualTo(1);
    await Assert.That(_handlings(otherHost, tick)).IsEqualTo(1);
  }

  /// <summary>
  /// Two differently named services that declare the same saga each handle every tick for it, and each checks its
  /// own saga state (#1005): the service whose saga has completed ends its chain, the one whose saga still runs
  /// re-arms it. There is no claim across services.
  /// </summary>
  [Test]
  public async Task Tick_TwoServicesDeclaringTheSameSaga_EachChecksItsOwnStateAsync() {
    var sagaId = Guid.CreateVersion7();
    var entityId = Guid.CreateVersion7();
    var completedHere = new BaseSagaModel {
      Id = sagaId,
      SagaName = ProbeSaga.SagaName,
      EntityId = entityId,
      TotalItems = 2,
      CompletedItems = 2,
      CompletionEventDispatched = true,
    };
    var runningThere = new BaseSagaModel {
      Id = sagaId,
      SagaName = ProbeSaga.SagaName,
      EntityId = entityId,
      TotalItems = 2,
      CompletedItems = 1,
    };
    await using var publisher = CapturedOutboxHost.Create(services => services.AddSingleton(_probeSagaState(completedHere)));
    await using var otherService = CapturedOutboxHost.Create(services => {
      services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.CreateVersion7(), "other-service", "host-b", 2));
      services.AddSingleton(_probeSagaState(runningThere));
    });
    var tick = new SagaCompletionWatchdogTickEvent { StreamId = sagaId, SagaName = ProbeSaga.SagaName, EntityId = entityId };

    await publisher.Dispatcher.PublishAsync(tick);
    var row = _row(publisher, tick);
    await _runStagesAsync(publisher, row.Rehydrate<SagaCompletionWatchdogTickEvent>(), [.. _stagesAfterTheOutbox, .. _stagesOfTheInbox]);
    await _runStagesAsync(otherService, row.Rehydrate<SagaCompletionWatchdogTickEvent>(), _stagesOfTheInbox);

    await Assert.That(publisher.Provider.GetRequiredService<PerServiceSagaState>().Outcomes).IsEquivalentTo([WatchdogTickOutcome.AlreadyComplete])
      .Because("the publishing service's own saga has completed, so its check ends the chain");
    await Assert.That(otherService.Provider.GetRequiredService<PerServiceSagaState>().Outcomes).IsEquivalentTo([WatchdogTickOutcome.ReArmed])
      .Because("the other service's saga is still running in its own state, so it checks it and re-arms");
    await Assert.That(_handlings(publisher, tick)).IsEqualTo(0);
    await Assert.That(_handlings(otherService, tick)).IsEqualTo(1);
  }

  /// <summary>A saga state for one service: a hand-written saga service reading that service's own projection.</summary>
  private static Func<IServiceProvider, PerServiceSagaState> _probeSagaState(BaseSagaModel saga) => sp => {
    var service = new ProbeSagaService(new DispatcherSagaEventEmitter(sp.GetRequiredService<IDispatcher>()), [new IncompleteSaga(saga, TenantId: null)]);
    return new PerServiceSagaState(service.TryRecoverViaWatchdogTickAsync);
  };
}

/// <summary>One service's saga state for <see cref="ProbeSaga"/>, and what each tick it handled concluded.</summary>
public sealed class PerServiceSagaState(Func<SagaCompletionWatchdogTickEvent, CancellationToken, Task<WatchdogTickOutcome>> check) {
  public List<WatchdogTickOutcome> Outcomes { get; } = [];

  public async Task HandleAsync(SagaCompletionWatchdogTickEvent tick, CancellationToken cancellationToken) {
    var outcome = await check(tick, cancellationToken);
    lock (Outcomes) {
      Outcomes.Add(outcome);
    }
  }
}

/// <summary>
/// A hand-written tick receiver for <see cref="ProbeSaga"/>, declared as the generated one is: at the receiving side,
/// for its own saga name. It checks the saga state of the service it runs in; a host that registers none ignores it.
/// </summary>
[FireAt(LifecycleStage.PreInboxInline)]
public sealed class PerServiceProbeSagaTickReceptor(IEnumerable<PerServiceSagaState> states) : IReceptor<SagaCompletionWatchdogTickEvent> {
  public async ValueTask HandleAsync(SagaCompletionWatchdogTickEvent message, CancellationToken cancellationToken = default) {
    if (message.SagaName != ProbeSaga.SagaName) {
      return;
    }
    foreach (var state in states) {
      await state.HandleAsync(message, cancellationToken);
    }
  }
}
