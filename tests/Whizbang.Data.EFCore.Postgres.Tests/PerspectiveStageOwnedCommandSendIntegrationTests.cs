// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
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
using Whizbang.Core.Signals;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.EFCore.Postgres.Tests.PerspectiveStageSends.Owned;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Data.EFCore.Postgres.Tests.PerspectiveStageSends;

/// <summary>
/// End to end for #1379 on the perspective path: an event that came from another service and has a
/// perspective in this one is applied by the REAL claim loop, <see cref="PerspectiveWorker"/> and
/// generated perspective runner against real PostgreSQL. A receptor firing at a perspective-path
/// lifecycle stage sends a command the service owns, handled here by a receptor that returns no
/// result. The command must be handled exactly once, in-process, and never written to the outbox.
/// Before the fix SendAsync answered Accepted and dropped the command. The case that surfaced the bug
/// was exactly this shape at PostAllPerspectivesDetached: a receptor reacting to another service's
/// event once every local perspective had applied it.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is the stored-form worker harness: <c>commit_handler_result</c> writes the event and its
/// perspective work row, the commit-order stamper's call runs in line, the claim loop polls
/// <c>claim_work</c>, the drain fetch leases through <c>get_stream_events</c>, and the generated
/// <c>OrderPerspective</c> runner writes <c>wh_per_order</c>. The event's envelope and stored metadata
/// carry a Current hop from <c>upstream-service</c>, so the worker sees it as another service's event.
/// </para>
/// <para>
/// Who fires each stage: the generated runner fires PrePerspectiveInline, PrePerspectiveDetached (on a
/// dedicated thread it joins before returning) and PostPerspectiveDetached; the worker fires
/// PostPerspectiveInline; with an <see cref="ILifecycleCoordinator"/> registered the worker's
/// post-batch task fires PostAllPerspectives and PostLifecycle, both variants, once every perspective
/// of the event has completed. The coordinator is registered here as a deployed service registers
/// it; without one only PostLifecycle fires and PostAllPerspectives never does.
/// </para>
/// <para>
/// The hook is registered at runtime on this test's own receptor registry (a fresh instance per
/// service provider), at exactly one stage, and is generic so the generator does not discover it.
/// Both the runner's stage gate and the worker's invoker consult the runtime registrations, so the
/// hook is found exactly where a compile-time <c>[FireAt]</c> receptor would be. Detached stages run
/// on their own task and scope, so every assertion waits on the owned command's completion signal and
/// on the perspective's applied signal, never on time.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleTrackingState.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard5")]
public class PerspectiveStageOwnedCommandSendIntegrationTests : EFCoreTestBase {
  private const string OWNED_DOMAIN = "Whizbang.Data.EFCore.Postgres.Tests.PerspectiveStageSends.Owned";
  private const string SERVICE_NAME = "perspective-stage-svc";
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(30);

  [Test]
  [Arguments(LifecycleStage.PrePerspectiveInline)]
  [Arguments(LifecycleStage.PrePerspectiveDetached)]
  [Arguments(LifecycleStage.PostPerspectiveInline)]
  [Arguments(LifecycleStage.PostPerspectiveDetached)]
  [Arguments(LifecycleStage.PostAllPerspectivesInline)]
  [Arguments(LifecycleStage.PostAllPerspectivesDetached)]
  [Arguments(LifecycleStage.PostLifecycleInline)]
  [Arguments(LifecycleStage.PostLifecycleDetached)]
  public async Task ForeignEventWithPerspective_StageSendsOwnedCommand_HandledOnceAsync(
      LifecycleStage stage) {
    await _assertOwnedCommandHandledOnceAsync(stage, asSystemForTenant: false);
  }

  [Test]
  [Arguments(LifecycleStage.PostAllPerspectivesDetached)]
  public async Task ForeignEventWithPerspective_StageSendsOwnedCommandAsSystem_HandledOnceAsync(
      LifecycleStage stage) {
    await _assertOwnedCommandHandledOnceAsync(stage, asSystemForTenant: true);
  }

  private async Task _assertOwnedCommandHandledOnceAsync(LifecycleStage stage, bool asSystemForTenant) {
    var ct = CancellationToken.None;
    var streamId = (Guid)TrackedGuid.New();
    var commandId = Guid.CreateVersion7();
    var handled = PerspectiveStageSendProbe.Expect(commandId);

    await using var pipeline = _composePipeline(streamId, commandId);
    var registry = pipeline.Services.GetRequiredService<IReceptorRegistry>();
    var hook = new PerspectiveStageSendHook<SampleOrderCreatedEvent>(
      pipeline.Services.GetRequiredService<IDispatcher>(),
      e => e.OrderId.Value == streamId,
      commandId,
      asSystemForTenant);
    registry.Register<SampleOrderCreatedEvent>(hook, stage);

    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;
    try {
      await using var conn = await _openAsync(ct);
      await pipeline.StartAsync(conn, ct);
      await pipeline.CommitForeignEventAsync(conn, streamId, ct);

      // The owned command's receptor completing is the transition under test; a send the
      // dispatcher dropped or threw on never completes it (the hook reports a throw as a fault,
      // and a perspective failure is reported the same way)
      await handled.Task.WaitAsync(_signalDeadline, ct);
      // The perspective applied the event: the stage ran on the real perspective path, and every
      // stage that fires before the applied announcement has had its one chance to fire again
      await pipeline.Applied.Task.WaitAsync(_signalDeadline, ct);

      await Assert.That(hook.Invocations).IsEqualTo(1)
        .Because("the hook is registered at one stage, and that stage fires once for the event");
      await Assert.That(PerspectiveStageSendProbe.CountFor(commandId)).IsEqualTo(1)
        .Because("the owned command must be handled exactly once, in-process");

      await using var dbContext = CreateDbContext();
      var outboxCopies = await dbContext.Outbox
        .CountAsync(m => m.MessageType.Contains(nameof(PerspectiveStageOwnedCommand)), ct);
      await Assert.That(outboxCopies).IsEqualTo(0)
        .Because("a command the service owns and handles never goes to the outbox");
    } finally {
      registry.Unregister<SampleOrderCreatedEvent>(hook, stage);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  // ============================================================================
  // pipeline (the StoredFormScalarMismatchWorkerTests composition, plus the dispatcher a
  // deployed service has: generated receptors and dispatcher, owned domains, a lifecycle
  // coordinator and a scoped work coordinator strategy for the outbox)
  // ============================================================================

  private Pipeline _composePipeline(Guid streamId, Guid commandId) {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var instanceProvider = new ServiceInstanceProvider(Guid.CreateVersion7(), SERVICE_NAME, "perspective-stage-host", 1);

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddScoped<IEventStore>(sp => new EFCoreEventStore<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddScoped<IWorkCoordinatorStrategy>(sp =>
      new ScopedWorkCoordinatorStrategy(
        coordinator: sp.GetRequiredService<IWorkCoordinator>(),
        instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
        workChannelWriter: null,
        options: new WorkCoordinatorOptions { LeaseSeconds = 30, AbandonStaleInstanceThresholdSeconds = 300, PartitionCount = 4 },
        logger: sp.GetRequiredService<ILogger<ScopedWorkCoordinatorStrategy>>(),
        inboxChannelWriter: new InboxChannelWriter()));
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    services.AddSingleton<ILifecycleCoordinator, LifecycleCoordinator>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddOptions<Whizbang.Core.Configuration.WhizbangCoreOptions>();
    services.AddPerspectiveRunners();
    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services, typeof(WorkCoordinationDbContext), typeof(Order), "wh_per_order",
      new PostgresUpsertStrategy());
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddWhizbangLifecycleMessageDeserializer();
    services.AddWhizbangMessageSecurity();
    services.AddMetrics();
    var provider = services.BuildServiceProvider();
    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var harness = new PerspectiveWorkerTestHarness();

    var claimWorker = new ClaimWorker(
      scopeFactory: scopeFactory,
      instanceProvider: instanceProvider,
      notificationListener: new NoOpWorkNotificationListener(),
      schemaReadyGate: gate,
      options: Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 50,
        PollingMaxIntervalMilliseconds = 200,
        AdaptiveClaimWindow = false,
        AdaptiveOutstandingBudget = false,
        MaxStreamsPerBatch = 100,
      }),
      logger: NullLogger<ClaimWorker>.Instance,
      outboxChannel: new WorkChannelWriter(),
      inboxChannel: new InboxChannelWriter(),
      perspectiveChannel: harness.ChannelWriter,
      perspectiveDrainChannel: harness.DrainChannel,
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: new InboxDrainChannel(),
      signalingGate: NullNotifySignalingGate.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      signalBus: NullSignalBus.Instance);

    var workerOptions = new PerspectiveWorkerOptions { MaxConcurrentDrainConsumers = 1 };
    var perspectiveWorker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: scopeFactory,
      options: Options.Create(workerOptions),
      schemaReadyGate: gate,
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: provider.GetRequiredService<IEventTypeProvider>(),
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
      perspectiveCompletionChannel: new WriteThroughCompletionChannel(scopeFactory),
      failureChannel: new FaultReportingFailureChannel(commandId),
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor(workerOptions));

    var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    perspectiveWorker.OnPerspectiveEventProcessed += e => {
      if (e.StreamId == streamId) {
        applied.TrySetResult();
      }
    };

    return new Pipeline {
      Services = provider,
      InstanceProvider = instanceProvider,
      ClaimWorker = claimWorker,
      PerspectiveWorker = perspectiveWorker,
      Applied = applied,
    };
  }

  private sealed class Pipeline : IAsyncDisposable {
    public required ServiceProvider Services { get; init; }
    public required ServiceInstanceProvider InstanceProvider { get; init; }
    public required ClaimWorker ClaimWorker { get; init; }
    public required PerspectiveWorker PerspectiveWorker { get; init; }
    public required TaskCompletionSource Applied { get; init; }
    private bool _started;

    public async Task StartAsync(NpgsqlConnection conn, CancellationToken ct) {
      await _registerInstanceAsync(conn, InstanceProvider.InstanceId, ct);
      await _seedPerspectiveAssociationAsync(conn, ct);
      _started = true;
      await ClaimWorker.StartAsync(ct);
      await PerspectiveWorker.StartAsync(ct);
      await PerspectiveWorker.StartupScanComplete.WaitAsync(_signalDeadline, ct);
    }

    /// <summary>
    /// Commits one sample order event on the stream, its envelope and stored metadata carrying a
    /// Current hop from another service, then stamps it the way the commit-order stamper would.
    /// </summary>
    public async Task CommitForeignEventAsync(NpgsqlConnection conn, Guid streamId, CancellationToken ct) {
      await using (var call = conn.CreateCommand()) {
        call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
        call.Parameters.AddWithValue("req", _buildCommitRequest(InstanceProvider, streamId, (Guid)TrackedGuid.New()));
        _ = await call.ExecuteScalarAsync(ct);
      }
      // The commit-order stamper's job, done in line: the claim gates unstamped rows behind a grace
      // window, and stamping here is what the stamper would do on the commit's wake.
      await using (var stamp = conn.CreateCommand()) {
        stamp.CommandText = "SELECT stamp_pending_commit_sequences(10000, false)";
        _ = await stamp.ExecuteScalarAsync(ct);
      }
    }

    private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId, CancellationToken ct) {
      await using var reg = conn.CreateCommand();
      reg.CommandText = """
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, @svc, 'perspective-stage-host', 1, NOW(), NOW(), '{}'::jsonb)
        ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()
        """;
      reg.Parameters.AddWithValue("id", instanceId);
      reg.Parameters.AddWithValue("svc", SERVICE_NAME);
      await reg.ExecuteNonQueryAsync(ct);
    }

    private static async Task _seedPerspectiveAssociationAsync(NpgsqlConnection conn, CancellationToken ct) {
      var eventType = TypeNameFormatter.Format(typeof(SampleOrderCreatedEvent));
      var perspectiveName = TypeNameFormatter.GetPerspectiveName(typeof(OrderPerspective));
      await using var assoc = conn.CreateCommand();
      assoc.CommandText = """
        INSERT INTO wh_message_associations
          (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
        VALUES (gen_random_uuid(), @eventType, 'perspective', @target, @svc, @eventType, NOW(), NOW())
        ON CONFLICT DO NOTHING
        """;
      assoc.Parameters.AddWithValue("eventType", eventType);
      assoc.Parameters.AddWithValue("target", perspectiveName);
      assoc.Parameters.AddWithValue("svc", SERVICE_NAME);
      await assoc.ExecuteNonQueryAsync(ct);
    }

    private static string _buildCommitRequest(IServiceInstanceProvider instance, Guid streamId, Guid eventId) {
      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      var eventType = TypeNameFormatter.Format(typeof(SampleOrderCreatedEvent));
      var payload = new SampleOrderCreatedEvent { OrderId = new TestOrderId(streamId), Amount = 42m };
      var payloadJson = JsonSerializer.Serialize(payload, jsonOptions.GetTypeInfo(typeof(SampleOrderCreatedEvent)));
      List<MessageHop> hops = [new MessageHop {
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
      }];
      var dispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox };
      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(eventId),
        DispatchContext = dispatchContext,
        Hops = hops,
        Payload = JsonDocument.Parse(payloadJson).RootElement
      };
      var envelopeJson = JsonSerializer.Serialize(envelope, jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
      // The metadata column is where the drain fetch reads hops back from, so the foreign hop is
      // stored there too, in the form the dispatcher writes it.
      var metadata = new EnvelopeMetadata { MessageId = MessageId.From(eventId), Hops = hops, DispatchContext = dispatchContext };
      var metadataJson = JsonSerializer.Serialize(metadata, jsonOptions.GetTypeInfo(typeof(EnvelopeMetadata)));

      return $$"""
        {
          "instance_id": "{{instance.InstanceId}}",
          "service_name": "{{instance.ServiceName}}",
          "host_name": "{{instance.HostName}}",
          "process_id": {{instance.ProcessId}},
          "new_outbox_messages": [
            {
              "MessageId": "{{eventId}}",
              "Destination": "order-events",
              "MessageType": "{{eventType}}",
              "EnvelopeType": null,
              "Envelope": {{envelopeJson}},
              "Metadata": {{metadataJson}},
              "Scope": null,
              "StreamId": "{{streamId}}",
              "IsEvent": true
            }
          ]
        }
        """;
    }

    public async ValueTask DisposeAsync() {
      if (_started) {
        await PerspectiveWorker.StopAsync(CancellationToken.None);
        await ClaimWorker.StopAsync(CancellationToken.None);
      }
      await Services.DisposeAsync();
    }
  }

  /// <summary>Completes each row and cursor at the coordinator before returning, as the flush worker would.</summary>
  private sealed class WriteThroughCompletionChannel(IServiceScopeFactory scopeFactory) : IPerspectiveCompletionChannel {
    public ValueTask EnqueueEventWorkIdAsync(Guid eventWorkId, CancellationToken cancellationToken = default) =>
      _completeAsync([], [eventWorkId], cancellationToken);

    public ValueTask EnqueueCursorAsync(PerspectiveCursorCompletion cursor, CancellationToken cancellationToken = default) =>
      _completeAsync([cursor], [], cancellationToken);

    private async ValueTask _completeAsync(PerspectiveCursorCompletion[] cursors, Guid[] workIds, CancellationToken ct) {
      await using var scope = scopeFactory.CreateAsyncScope();
      await scope.ServiceProvider.GetRequiredService<IWorkCoordinator>()
        .CompletePerspectiveAsync(cursors, workIds, debugMode: false, ct);
    }
  }

  /// <summary>
  /// A perspective failure ends the test with its cause: the event that should have fired the stage
  /// was not applied, so waiting for the owned command would only time out.
  /// </summary>
  private sealed class FaultReportingFailureChannel(Guid commandId) : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      PerspectiveStageSendProbe.Failed(commandId, new InvalidOperationException(
        $"The perspective pipeline reported a {category} failure for {failure.MessageId}: {failure.Error}"));
      return ValueTask.CompletedTask;
    }
  }

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }
}
