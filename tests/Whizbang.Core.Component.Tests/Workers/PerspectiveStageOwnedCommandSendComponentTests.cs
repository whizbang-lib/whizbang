// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.PerspectiveStageSends.Owned;
using Whizbang.Core.Component.Tests.PerspectiveStageSends.Upstream;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Core.Component.Tests.PerspectiveStageSends;

/// <summary>
/// Component proof for #1379 on the perspective path: the REAL <see cref="PerspectiveWorker"/> applies
/// another service's event that has a perspective in this service, the REAL generated dispatcher,
/// <see cref="ReceptorInvoker"/>, generated receptor registry and <see cref="LifecycleCoordinator"/>
/// fire a receptor at a perspective-path lifecycle stage, and that receptor sends a command the
/// service owns, handled here by a receptor that returns no result. The command must be handled
/// exactly once, in-process, and never queued to the outbox. Before the fix SendAsync answered
/// Accepted and dropped such a command. The case that surfaced the bug was this shape at
/// PostAllPerspectivesDetached.
/// </summary>
/// <remarks>
/// <para>
/// Only the storage edges are fakes: an in-memory coordinator and event store, a runner that stands in
/// for the generated one, and a recording <see cref="IWorkCoordinatorStrategy"/> in place of the
/// dispatcher's outbox. The work goes through the worker's per-event channel path, in which the worker
/// itself fires PrePerspectiveInline and PrePerspectiveDetached (through the coordinator's tracking),
/// PostPerspectiveInline, and, from its post-batch task once every perspective of the event has
/// completed, PostAllPerspectives and PostLifecycle in both variants. Owned domains are configured
/// the way a deployed service configures them, and the trigger's envelope carries a Current hop from
/// another service.
/// </para>
/// <para>
/// PostPerspectiveDetached is never fired by the worker (see the note in
/// <c>PerspectiveWorker._reportCompletionAndSignalSyncAsync</c>): only the generated perspective runner
/// fires it, as do the PrePerspective stages on the worker's drain path. This project declares no
/// perspective, so no generated runner exists to run here. The PostPerspectiveDetached test below
/// therefore uses a runner that fires the stage the way the runner template does (the same registry
/// gate, a fresh scope's invoker, a dedicated thread awaited before the run returns): it proves the
/// send from a receptor invoked at that stage with that shape, not the template itself. The generated
/// runner is exercised for real by the Postgres integration test of the same name.
/// </para>
/// <para>
/// The hook is registered at runtime on the test's own registry at exactly one stage. Detached stages
/// run on their own task and scope, so every assertion waits on the owned command's completion signal,
/// the trigger's completion report and the worker's post-batch task, never on time.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleTrackingState.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class PerspectiveStageOwnedCommandSendComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.PerspectiveStageSends.Owned";
  private const string PERSPECTIVE_NAME = "PerspectiveStageSends.TriggerPerspective";
  private static readonly TimeSpan _signalTimeout = TimeSpan.FromSeconds(30);

  [Test]
  [Arguments(LifecycleStage.PrePerspectiveInline)]
  [Arguments(LifecycleStage.PrePerspectiveDetached)]
  [Arguments(LifecycleStage.PostPerspectiveInline)]
  [Arguments(LifecycleStage.PostAllPerspectivesInline)]
  [Arguments(LifecycleStage.PostAllPerspectivesDetached)]
  [Arguments(LifecycleStage.PostLifecycleInline)]
  [Arguments(LifecycleStage.PostLifecycleDetached)]
  public async Task WorkerStage_SendsOwnedCommand_HandledOnceNotQueuedAsync(
      LifecycleStage stage) {
    await _assertOwnedCommandHandledOnceAsync(stage, asSystemForTenant: false);
  }

  [Test]
  [Arguments(LifecycleStage.PostAllPerspectivesDetached)]
  public async Task WorkerStage_SendsOwnedCommandAsSystem_HandledOnceNotQueuedAsync(
      LifecycleStage stage) {
    await _assertOwnedCommandHandledOnceAsync(stage, asSystemForTenant: true);
  }

  [Test]
  public async Task RunnerPostPerspectiveDetached_SendsOwnedCommand_HandledOnceNotQueuedAsync() {
    await _assertOwnedCommandHandledOnceAsync(LifecycleStage.PostPerspectiveDetached, asSystemForTenant: false);
  }

  private static async Task _assertOwnedCommandHandledOnceAsync(LifecycleStage stage, bool asSystemForTenant) {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var commandId = Guid.CreateVersion7();
    var handled = PerspectiveStageSendProbe.Expect(commandId);

    var instance = new FixedServiceInstanceProvider();
    var outbox = new RecordingOutbox();
    var coordinator = new SingleStreamWorkCoordinator();
    var eventStore = new SingleEventStore(streamId, eventId);
    var eventTypeProvider = new FixedEventTypeProvider([typeof(PerspectiveStageTrigger)]);
    var runnerRegistry = new TriggerPerspectiveRunnerRegistry(eventStore);
    var harness = new PerspectiveWorkerTestHarness();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider>(instance);
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IEventTypeProvider>(eventTypeProvider);
    services.AddSingleton<IPerspectiveRunnerRegistry>(runnerRegistry);
    services.AddSingleton<ILifecycleCoordinator, LifecycleCoordinator>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddWhizbangMessageSecurity();
    await using var serviceProvider = services.BuildServiceProvider();

    var registry = serviceProvider.GetRequiredService<IReceptorRegistry>();
    var hook = new PerspectiveStageSendHook<PerspectiveStageTrigger>(
      serviceProvider.GetRequiredService<IDispatcher>(),
      e => e.OrderId == streamId,
      commandId,
      asSystemForTenant);
    registry.Register<PerspectiveStageTrigger>(hook, stage);

    var workerOptions = new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 };
    var worker = new PerspectiveWorker(
      instanceProvider: instance,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(workerOptions),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: eventTypeProvider,
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: NullLogger<PerspectiveWorker>.Instance,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: new FaultReportingFailureChannel(commandId),
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor(workerOptions));

    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;
    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await harness.EnqueueWorkAsync(new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = PERSPECTIVE_NAME,
        LastProcessedEventId = null,
        PartitionNumber = 1
      }, cts.Token);

      // The owned command's receptor completing is the transition under test; a send the
      // dispatcher dropped or threw on never completes it (the hook reports a throw as a fault,
      // and a perspective failure is reported the same way)
      await handled.Task.WaitAsync(_signalTimeout);
      // The trigger's cursor completion was reported: every stage up to PostPerspectiveInline has
      // run for it. Then the worker's post-batch task, which runs PostAllPerspectives and
      // PostLifecycle, has finished: no inline stage can still fire the hook a second time.
      await coordinator.CompletionReported.WaitAsync(_signalTimeout);
      if (worker.PendingPostLifecycle is { } postLifecycle) {
        await postLifecycle.WaitAsync(_signalTimeout);
      }

      await Assert.That(hook.Invocations).IsEqualTo(1)
        .Because("the hook is registered at one stage, and that stage fires once for the event");
      await Assert.That(PerspectiveStageSendProbe.CountFor(commandId)).IsEqualTo(1)
        .Because("the owned command must be handled exactly once, in-process");
      await Assert.That(outbox.QueuedOf(typeof(PerspectiveStageOwnedCommand))).IsEmpty()
        .Because("a command the service owns and handles never goes to the outbox");
    } finally {
      registry.Unregister<PerspectiveStageTrigger>(hook, stage);
      await cts.CancelAsync();
      if (worker.ExecuteTask is { } body) {
        await body.WaitAsync(_signalTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      }
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  // --- fakes (storage edges and the runner only) ---

  private sealed class FixedServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "perspective-stage-svc";
    public string HostName => "perspective-stage-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class FixedEventTypeProvider(IReadOnlyList<Type> eventTypes) : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
  }

  /// <summary>No stored cursor, and a signal when the worker reports the trigger's cursor completion.</summary>
  private sealed class SingleStreamWorkCoordinator : IWorkCoordinator {
    private readonly TaskCompletionSource _completionReported = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task CompletionReported => _completionReported.Task;

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      _completionReported.TrySetResult();
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>One stream holding the trigger; every read deserializes a fresh envelope, as a store does.</summary>
  private sealed class SingleEventStore(Guid storedStreamId, Guid storedEventId) : IEventStore {
    public Guid StreamId => storedStreamId;
    public Guid EventId => storedEventId;

    public MessageEnvelope<IEvent> Read() => _triggerEnvelope(storedStreamId, storedEventId);

    /// <summary>The trigger as the event store hands it back: a Current hop from another service, with its tenant.</summary>
    private static MessageEnvelope<IEvent> _triggerEnvelope(Guid streamId, Guid eventId) => new() {
      MessageId = MessageId.From(eventId),
      Payload = new PerspectiveStageTrigger(streamId),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.CreateVersion7(),
          ServiceName = "upstream-service",
          HostName = "upstream-host",
          ProcessId = 1
        },
        CorrelationId = CorrelationId.New(),
        CausationId = MessageId.New(),
        Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-perspective-stage-sends", UserId = "user-perspective-stage-sends" })
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
    };

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(streamId == StreamId && afterEventId != EventId ? new List<MessageEnvelope<IEvent>> { Read() } : []);

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }

  /// <summary>One perspective handling the trigger type, so the worker expects exactly one completion per event.</summary>
  private sealed class TriggerPerspectiveRunnerRegistry(SingleEventStore eventStore) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      perspectiveName == PERSPECTIVE_NAME ? new TemplateShapedRunner(eventStore, serviceProvider) : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [
      new PerspectiveRegistrationInfo(PERSPECTIVE_NAME, "global::" + PERSPECTIVE_NAME, "global::PerspectiveStageSends.TriggerModel",
        [TypeNameFormatter.Format(typeof(PerspectiveStageTrigger))])
    ];

    public IReadOnlyList<Type> GetEventTypes() => [typeof(PerspectiveStageTrigger)];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  /// <summary>
  /// Applies the trigger and, after the apply, fires PostPerspectiveDetached the way the runner
  /// template does (PerspectiveRunnerTemplate.cs, the post-flush loop): only when a receptor is
  /// registered at the stage for the event type, from a fresh scope's invoker, on a dedicated
  /// thread awaited before the run returns.
  /// </summary>
  private sealed class TemplateShapedRunner(SingleEventStore eventStore, IServiceProvider serviceProvider) : IPerspectiveRunner {
    public Type PerspectiveType => typeof(TemplateShapedRunner);

    public async Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) {
      var envelope = eventStore.Read();
      var registry = serviceProvider.GetService<IReceptorRegistry>();
      if (registry?.GetReceptorsFor(envelope.Payload.GetType(), LifecycleStage.PostPerspectiveDetached).Count > 0) {
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var context = new LifecycleExecutionContext {
          CurrentStage = LifecycleStage.PostPerspectiveDetached,
          StreamId = streamId,
          PerspectiveType = PerspectiveType,
          EventId = envelope.MessageId.Value,
          LastProcessedEventId = envelope.MessageId.Value
        };
        await BackgroundStageDispatch.StartLongRunning(async () => {
          await using var lifecycleScope = scopeFactory.CreateAsyncScope();
          var invoker = lifecycleScope.ServiceProvider.GetService<IReceptorInvoker>();
          if (invoker is not null) {
            await invoker.InvokeAsync(envelope, LifecycleStage.PostPerspectiveDetached, context, cancellationToken);
          }
        }, cancellationToken);
      }
      return new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        PerspectiveType = PerspectiveType,
        LastEventId = eventStore.EventId,
        Status = PerspectiveProcessingStatus.Completed
      };
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  /// <summary>
  /// A perspective failure ends the test with its cause: the event that should have fired the stage
  /// was not applied, so waiting for the owned command would only time out.
  /// </summary>
  private sealed class FaultReportingFailureChannel(Guid commandId) : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      PerspectiveStageSendProbe.Failed(commandId, new InvalidOperationException(
        $"The perspective worker reported a {category} failure for {failure.MessageId}: {failure.Error}"));
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>The dispatcher's outbox: records every message it is asked to queue.</summary>
  private sealed class RecordingOutbox : IWorkCoordinatorStrategy {
    private readonly List<OutboxMessage> _queued = [];

    public IReadOnlyList<OutboxMessage> QueuedOf(Type messageType) {
      lock (_queued) {
        return [.. _queued.Where(m => m.MessageType.Contains(messageType.Name, StringComparison.Ordinal))];
      }
    }

    public void QueueOutboxMessage(OutboxMessage message) {
      lock (_queued) {
        _queued.Add(message);
      }
    }

    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => FlushAndGetBatchAsync(flags, ct);

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }
}
