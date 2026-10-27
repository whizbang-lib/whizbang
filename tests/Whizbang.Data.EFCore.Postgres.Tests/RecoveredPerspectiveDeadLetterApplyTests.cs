// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Signals;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A perspective work row that was dead-lettered and then recovered is applied by the perspective, and its dead letter
/// reads Recovered only once it has been applied (#1722).
/// </summary>
/// <remarks>
/// <para>
/// Recovery re-inserted the work row with a column list of its own and marked the dead letter Recovered in the same
/// call, before the perspective had applied anything. It now re-enters through the perspective work store, and the
/// completion that retires the row settles the dead letter.
/// </para>
/// <para>
/// Real PostgreSQL and the real pieces throughout: <c>commit_handler_result</c> writes the event and its work row, the
/// dead-letter function moves the row, one scan of the recovery worker over the Postgres recovery service re-drives
/// it, and the real claim loop, <see cref="PerspectiveWorker"/> and generated <c>OrderPerspective</c> runner apply it to
/// <c>wh_per_order</c>. The completion channel writes through to the coordinator and signals each completed row, so
/// every wait is on the transition it asserts.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/recovery</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterRecoveryService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/DeadLetterRecoveryWorker.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard5")]
public class RecoveredPerspectiveDeadLetterApplyTests : EFCoreTestBase {
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(60);

  [Test]
  public async Task ARecoveredPerspectiveWorkRow_IsApplied_AndReadsRecoveredOnceAppliedAsync() {
    var ct = CancellationToken.None;
    var streamId = (Guid)TrackedGuid.New();
    await using var pipeline = _composePipeline();
    await using var conn = await _openAsync(ct);
    await pipeline.RegisterAsync(conn, ct);

    var eventId = await pipeline.CommitAsync(conn, streamId, amount: 42m, ct);
    var workId = await _scalarAsync<Guid>(conn, "SELECT event_work_id FROM wh_perspective_events WHERE event_id = @id", eventId, ct);
    var deadLetterId = await _deadLetterAsync(conn, workId, ct);
    await _recoverAsync(pipeline.Services, deadLetterId);
    var afterRecovery = await _scalarAsync<int>(conn, "SELECT recovery_status FROM wh_dead_letters WHERE dead_letter_id = @id", deadLetterId, ct);

    var completed = pipeline.CompletionOf(workId);
    await pipeline.StartAsync(ct);
    await completed.WaitAsync(_signalDeadline, ct);

    await Assert.That(await _scalarAsync<decimal>(conn,
      "SELECT (data ->> 'Amount')::numeric FROM wh_per_order WHERE id = @id", streamId, ct)).IsEqualTo(42m)
      .Because("the perspective applied the recovered event to its read model");
    await Assert.That(afterRecovery).IsEqualTo((int)DeadLetterRecoveryStatus.Recovering)
      .Because("the dead letter reads Recovered only once the event has been applied, not when it re-enters");
    await Assert.That(await _scalarAsync<int>(conn,
      "SELECT recovery_status FROM wh_dead_letters WHERE dead_letter_id = @id", deadLetterId, ct))
      .IsEqualTo((int)DeadLetterRecoveryStatus.Recovered)
      .Because("the completion that retired the applied row settled its dead letter");
  }

  // ===== Helpers =====

  /// <summary>Moves the work row into dead-letter custody as the perspective worker's attempts gate does.</summary>
  private static async Task<Guid> _deadLetterAsync(NpgsqlConnection conn, Guid workId, CancellationToken ct) {
    var deadLetterId = (Guid)TrackedGuid.New();
    await using var command = conn.CreateCommand();
    command.CommandText = "SELECT move_to_dead_letters(@dl, 'wh_perspective_events', @id, @reason, 'apply failed', @inst, 'test/1')";
    command.Parameters.AddWithValue("dl", deadLetterId);
    command.Parameters.AddWithValue("id", workId);
    command.Parameters.AddWithValue("reason", (int)MessageFailureReason.MaxAttemptsExceeded);
    command.Parameters.AddWithValue("inst", Guid.CreateVersion7());
    await Assert.That(await command.ExecuteScalarAsync(ct)).IsEqualTo(deadLetterId);
    return deadLetterId;
  }

  /// <summary>Runs one scan of the recovery worker, which re-drives every due dead letter.</summary>
  private static async Task _recoverAsync(ServiceProvider provider, Guid deadLetterId) {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new DeadLetterRecoveryWorker(
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      schemaReadyGate: gate,
      options: Options.Create(new DeadLetterRecoveryOptions { ScanIntervalMinutes = 60, ScanBatchSize = 50, EnableGenerationReplay = false }),
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      generationProvider: new DefaultGenerationProvider(),
      logger: NullLogger<DeadLetterRecoveryWorker>.Instance,
      notificationListener: new NoOpWorkNotificationListener());
    var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnScanCompleted += () => scanned.TrySetResult();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    try {
      await scanned.Task.WaitAsync(_signalDeadline);
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
    }
    await Assert.That(worker.TotalRecovered).IsEqualTo(1L)
      .Because($"the scan re-drives dead letter {deadLetterId}");
  }

  private Pipeline _composePipeline() {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddScoped<IEventStore>(sp => new EFCoreEventStore<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddOptions<Whizbang.Core.Configuration.WhizbangCoreOptions>();
    services.AddPerspectiveRunners();
    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services, typeof(WorkCoordinationDbContext), typeof(Order), "wh_per_order",
      new PostgresUpsertStrategy());
    // The recovery service the Postgres driver registers, and the default recovery policy.
    services.AddScoped<IDeadLetterRecoveryService>(sp =>
      new EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddSingleton<IDeadLetterRecoveryPolicy>(
      new DefaultDeadLetterRecoveryPolicy(Options.Create(new DeadLetterRecoveryOptions())));
    services.AddMetrics();
    var provider = services.BuildServiceProvider();
    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var instanceProvider = new ServiceInstanceProvider(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    var harness = new PerspectiveWorkerTestHarness();
    var completion = new SignalingCompletionChannel(scopeFactory);

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
      perspectiveCompletionChannel: completion,
      failureChannel: new FailingFailureChannel(completion),
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor(workerOptions));

    return new Pipeline {
      Services = provider,
      InstanceProvider = instanceProvider,
      ClaimWorker = claimWorker,
      PerspectiveWorker = perspectiveWorker,
      Completion = completion,
    };
  }

  private sealed class Pipeline : IAsyncDisposable {
    public required ServiceProvider Services { get; init; }
    public required ServiceInstanceProvider InstanceProvider { get; init; }
    public required ClaimWorker ClaimWorker { get; init; }
    public required PerspectiveWorker PerspectiveWorker { get; init; }
    public required SignalingCompletionChannel Completion { get; init; }
    private bool _started;

    public Task CompletionOf(Guid workId) => Completion.WaitForAsync(workId);

    /// <summary>Registers this instance and the perspective's association with the event type.</summary>
    public async Task RegisterAsync(NpgsqlConnection conn, CancellationToken ct) {
      await using (var reg = conn.CreateCommand()) {
        reg.CommandText = """
          INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
          VALUES (@id, 'recovered-perspective-svc', 'recovered-perspective-host', 1, NOW(), NOW(), '{}'::jsonb)
          ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()
          """;
        reg.Parameters.AddWithValue("id", InstanceProvider.InstanceId);
        await reg.ExecuteNonQueryAsync(ct);
      }
      var eventType = TypeNameFormatter.Format(typeof(SampleOrderCreatedEvent));
      await using var assoc = conn.CreateCommand();
      assoc.CommandText = """
        INSERT INTO wh_message_associations
          (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
        VALUES (gen_random_uuid(), @eventType, 'perspective', @target, 'recovered-perspective-svc', @eventType, NOW(), NOW())
        ON CONFLICT DO NOTHING
        """;
      assoc.Parameters.AddWithValue("eventType", eventType);
      assoc.Parameters.AddWithValue("target", TypeNameFormatter.GetPerspectiveName(typeof(OrderPerspective)));
      await assoc.ExecuteNonQueryAsync(ct);
    }

    public async Task StartAsync(CancellationToken ct) {
      _started = true;
      await ClaimWorker.StartAsync(ct);
      await PerspectiveWorker.StartAsync(ct);
      await PerspectiveWorker.StartupScanComplete.WaitAsync(_signalDeadline, ct);
    }

    /// <summary>Commits one order event on the stream and stamps it as the commit-order stamper would; returns its id.</summary>
    public async Task<Guid> CommitAsync(NpgsqlConnection conn, Guid streamId, decimal amount, CancellationToken ct) {
      var eventId = (Guid)TrackedGuid.New();
      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      var payload = new SampleOrderCreatedEvent { OrderId = new TestOrderId(streamId), Amount = amount };
      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(eventId),
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [],
        Payload = JsonSerializer.SerializeToElement(payload, jsonOptions.GetTypeInfo(typeof(SampleOrderCreatedEvent))),
      };
      var envelopeJson = JsonSerializer.Serialize(envelope, jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
      var request = $$"""
        {
          "instance_id": "{{InstanceProvider.InstanceId}}",
          "service_name": "{{InstanceProvider.ServiceName}}",
          "host_name": "{{InstanceProvider.HostName}}",
          "process_id": {{InstanceProvider.ProcessId}},
          "new_outbox_messages": [{
            "MessageId": "{{eventId}}",
            "Destination": "order-events",
            "MessageType": "{{TypeNameFormatter.Format(typeof(SampleOrderCreatedEvent))}}",
            "EnvelopeType": null,
            "Envelope": {{envelopeJson}},
            "Metadata": {},
            "Scope": null,
            "StreamId": "{{streamId}}",
            "IsEvent": true
          }]
        }
        """;
      await using (var call = conn.CreateCommand()) {
        call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
        call.Parameters.AddWithValue("req", request);
        _ = await call.ExecuteScalarAsync(ct);
      }
      await using (var stamp = conn.CreateCommand()) {
        stamp.CommandText = "SELECT stamp_pending_commit_sequences(10000, false)";
        _ = await stamp.ExecuteScalarAsync(ct);
      }
      return eventId;
    }

    public async ValueTask DisposeAsync() {
      if (_started) {
        await PerspectiveWorker.StopAsync(CancellationToken.None);
        await ClaimWorker.StopAsync(CancellationToken.None);
      }
      await Services.DisposeAsync();
    }
  }

  /// <summary>Completes each row and cursor at the coordinator before returning, then signals each completed row.</summary>
  private sealed class SignalingCompletionChannel(IServiceScopeFactory scopeFactory) : IPerspectiveCompletionChannel {
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _completed = new();

    public Task WaitForAsync(Guid workId) => _signal(workId).Task;

    /// <summary>Ends every wait with the failure, so a test fails with its cause instead of timing out.</summary>
    public void Fail(Exception failure) {
      foreach (var signal in _completed.Values) {
        signal.TrySetException(failure);
      }
    }

    public ValueTask EnqueueEventWorkIdAsync(Guid eventWorkId, CancellationToken cancellationToken = default) =>
      _completeAsync([], [eventWorkId], cancellationToken);

    public ValueTask EnqueueCursorAsync(PerspectiveCursorCompletion cursor, CancellationToken cancellationToken = default) =>
      _completeAsync([cursor], [], cancellationToken);

    private async ValueTask _completeAsync(PerspectiveCursorCompletion[] cursors, Guid[] workIds, CancellationToken ct) {
      await using (var scope = scopeFactory.CreateAsyncScope()) {
        await scope.ServiceProvider.GetRequiredService<IWorkCoordinator>()
          .CompletePerspectiveAsync(cursors, workIds, debugMode: false, ct);
      }
      foreach (var workId in workIds) {
        _signal(workId).TrySetResult();
      }
    }

    private TaskCompletionSource _signal(Guid workId) =>
      _completed.GetOrAdd(workId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
  }

  /// <summary>A perspective failure ends the test with its cause: the event that should have been applied was not.</summary>
  private sealed class FailingFailureChannel(SignalingCompletionChannel completion) : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      completion.Fail(new InvalidOperationException(
        $"The perspective pipeline reported a {category} failure for {failure.MessageId}: {failure.Error}"));
      return ValueTask.CompletedTask;
    }
  }

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, Guid id, CancellationToken ct) {
    await using var command = conn.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue(nameof(id), id);
    return (T)(await command.ExecuteScalarAsync(ct))!;
  }
}
