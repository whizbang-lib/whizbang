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
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Set-based perspective writes through the real pipeline (#2246): real PostgreSQL, the real claim loop, the
/// <see cref="PerspectiveWorker"/> and the generated <c>OrderPerspective</c> runner over <c>wh_per_order</c>, with every
/// default seam the driver registers. Each test commits its events, lets the claim loop hand every stream to the worker's
/// queues, and only then starts the worker, so one worker batch holds them all; the statements against the perspective
/// table are counted from Npgsql's own tracing, never timed, and every wait is on the completion or failure it asserts.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes</docs>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.ClaimedGroups.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.DrainGroups.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveApplyBatch.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class SetBasedPerspectiveWorkerIntegrationTests : EFCoreTestBase {
  private const string TABLE = "wh_per_order";
  private const int STREAMS = 12;
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(60);

  [Test]
  public async Task NewStreams_CostOneInsertForTheTable_AndNoRowReadAsync() {
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    await using var pipeline = _composePipeline(PerspectiveWriteMode.SetBased);
    await pipeline.RegisterAsync(conn, ct);
    var streams = Enumerable.Range(0, STREAMS).Select(_ => (Guid)TrackedGuid.New()).ToList();
    var work = new List<Guid>();
    for (var i = 0; i < streams.Count; i++) {
      work.Add(await pipeline.CommitAsync(conn, streams[i], amount: i + 1, ct));
    }

    await pipeline.HandOffAsync(streams, ct);
    using var statements = new NpgsqlStatementLog(ConnectionString);
    await pipeline.StartWorkerAsync(ct);
    await pipeline.Completion.WaitForAllAsync(work);

    var touching = statements.Matching(TABLE);
    await Assert.That(touching).Count().IsEqualTo(1)
      .Because("every stream of the batch is new, so none is read, and their rows are written with one statement");
    await Assert.That(touching[0]).Contains("INSERT INTO");
    var sequenceReads = statements.Matching("commit_sequence", "wh_event_store").Where(s => s.Contains("event_id", StringComparison.Ordinal)).ToList();
    await Assert.That(sequenceReads.Count(s => s.Contains("ANY", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("the commit sequences the rows record are read with one statement for the batch");
    await Assert.That(sequenceReads.Count(s => s.Contains("LIMIT", StringComparison.Ordinal))).IsEqualTo(0)
      .Because("no pair reads its own commit sequence");
    for (var i = 0; i < streams.Count; i++) {
      await Assert.That(await _amountAsync(conn, streams[i], ct)).IsEqualTo(i + 1m);
    }
  }

  [Test]
  public async Task AMixedBatch_ReadsTheTableOnce_AndWritesEachShapeWithOneStatementAsync() {
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    var existing = Enumerable.Range(0, STREAMS / 2).Select(_ => (Guid)TrackedGuid.New()).ToList();
    ServiceInstanceProvider instance;
    await using (var first = _composePipeline(PerspectiveWriteMode.SetBased)) {
      instance = first.InstanceProvider;
      await first.RegisterAsync(conn, ct);
      var firstWork = new List<Guid>();
      foreach (var stream in existing) {
        firstWork.Add(await first.CommitAsync(conn, stream, amount: 1, ct));
      }
      await first.HandOffAsync(existing, ct);
      await first.StartWorkerAsync(ct);
      await first.Completion.WaitForAllAsync(firstWork);
    }

    await using var pipeline = _composePipeline(PerspectiveWriteMode.SetBased, instance);
    var fresh = Enumerable.Range(0, STREAMS / 2).Select(_ => (Guid)TrackedGuid.New()).ToList();
    var work = new List<Guid>();
    foreach (var stream in existing) {
      work.Add(await pipeline.CommitAsync(conn, stream, amount: 50, ct));
    }
    foreach (var stream in fresh) {
      work.Add(await pipeline.CommitAsync(conn, stream, amount: 7, ct));
    }

    await pipeline.HandOffAsync([.. existing, .. fresh], ct);
    using var statements = new NpgsqlStatementLog(ConnectionString);
    await pipeline.StartWorkerAsync(ct);
    await pipeline.Completion.WaitForAllAsync(work);

    var touching = statements.Matching(TABLE);
    await Assert.That(touching.Count(s => s.Contains("xmin", StringComparison.Ordinal) && s.Contains("ANY", StringComparison.Ordinal)))
      .IsEqualTo(1).Because("the existing rows' versions are read with one statement");
    await Assert.That(touching.Count(s => s.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))).IsEqualTo(1)
      .Because("the new rows are inserted with one statement");
    await Assert.That(touching.Count(s => s.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))).IsEqualTo(1)
      .Because("the existing rows are updated on their read version with one statement");
    await Assert.That(touching).Count().IsLessThanOrEqualTo(4)
      .Because("one version read, one model read, one insert and one update: nothing per stream");
    foreach (var stream in existing) {
      await Assert.That(await _amountAsync(conn, stream, ct)).IsEqualTo(50m);
    }
    foreach (var stream in fresh) {
      await Assert.That(await _amountAsync(conn, stream, ct)).IsEqualTo(7m);
    }
  }

  [Test]
  public async Task ARowTheDatabaseRejects_FailsAlone_AndTheBatchsOtherRowsAreWrittenAsync() {
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    await using (var constraint = conn.CreateCommand()) {
      constraint.CommandText = $"ALTER TABLE {TABLE} ADD CONSTRAINT ck_set_based_rejects_13 CHECK ((data ->> 'Amount')::numeric <> 13)";
      await constraint.ExecuteNonQueryAsync(ct);
    }
    await using var pipeline = _composePipeline(PerspectiveWriteMode.SetBased);
    await pipeline.RegisterAsync(conn, ct);
    var streams = Enumerable.Range(0, 4).Select(_ => (Guid)TrackedGuid.New()).ToList();
    var healthy = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      healthy.Add(await pipeline.CommitAsync(conn, streams[i], amount: i + 1, ct));
    }
    var rejected = await pipeline.CommitAsync(conn, streams[3], amount: 13, ct);

    await pipeline.HandOffAsync(streams, ct);
    await pipeline.StartWorkerAsync(ct);
    await pipeline.Completion.WaitForAllAsync(healthy);
    var failure = await pipeline.Failures.WaitForAsync(rejected);

    await Assert.That(failure.Error).Contains("ck_set_based_rejects_13")
      .Because("the rejected row is applied again per pair, where it fails exactly as it always did");
    await Assert.That(await _rowExistsAsync(conn, streams[3], ct)).IsFalse();
    for (var i = 0; i < 3; i++) {
      await Assert.That(await _amountAsync(conn, streams[i], ct)).IsEqualTo(i + 1m)
        .Because("a row the database rejects fails alone; the batch's other rows are written");
    }
  }

  [Test]
  public async Task PerStreamMode_WritesEachStreamWithItsOwnStatements_AsBeforeAsync() {
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    await using var pipeline = _composePipeline(PerspectiveWriteMode.PerStream);
    await pipeline.RegisterAsync(conn, ct);
    var streams = Enumerable.Range(0, 4).Select(_ => (Guid)TrackedGuid.New()).ToList();
    var work = new List<Guid>();
    foreach (var stream in streams) {
      work.Add(await pipeline.CommitAsync(conn, stream, amount: 3, ct));
    }

    await pipeline.HandOffAsync(streams, ct);
    using var statements = new NpgsqlStatementLog(ConnectionString);
    await pipeline.StartWorkerAsync(ct);
    await pipeline.Completion.WaitForAllAsync(work);

    var touching = statements.Matching(TABLE);
    await Assert.That(touching.Count(s => s.Contains("ANY(", StringComparison.Ordinal))).IsEqualTo(0)
      .Because("the per-stream mode reads no row in a batch");
    await Assert.That(touching.Count(s => s.Contains("INSERT", StringComparison.OrdinalIgnoreCase))).IsEqualTo(streams.Count)
      .Because("the per-stream mode writes each stream's row with a statement of its own, as before set-based writes");
    foreach (var stream in streams) {
      await Assert.That(await _amountAsync(conn, stream, ct)).IsEqualTo(3m);
    }
  }

  // ===== Harness =====

  private Pipeline _composePipeline(PerspectiveWriteMode mode, ServiceInstanceProvider? instance = null) {
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
      services, typeof(WorkCoordinationDbContext), typeof(Order), TABLE, new PostgresUpsertStrategy());
    // The set-based write's driver seams, as the Postgres driver registers them for the service's context.
    services.AddSingleton<IPerspectiveApplyCoordinator>(new PerspectiveApplyCoordinator(NullLogger<PerspectiveApplyCoordinator>.Instance));
    services.AddSingleton<IPerspectiveStateSource>(new BatchedPerspectiveStateSource());
    services.AddKeyedScoped<IPerspectiveWriteSink>(typeof(WorkCoordinationDbContext), (sp, _) =>
      new EFCorePostgresPerspectiveWriteSink(sp.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddMetrics();
    var provider = services.BuildServiceProvider();
    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

    // The same instance across two pipelines of one test, so the second claims the streams the first one owned.
    var instanceProvider = instance ?? new ServiceInstanceProvider(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    var harness = new PerspectiveWorkerTestHarness();
    var handedOff = new HandedOffStreams();
    var completion = new SignalingCompletionChannel(scopeFactory);
    var failures = new RecordingFailureChannel(scopeFactory);

    var claimWorker = new ClaimWorker(
      scopeFactory: scopeFactory,
      instanceProvider: instanceProvider,
      notificationListener: new NoOpWorkNotificationListener(),
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
      perspectiveChannel: new CountingWorkChannel(harness.ChannelWriter, handedOff),
      perspectiveDrainChannel: new CountingDrainChannel(harness.DrainChannel, handedOff),
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: new InboxDrainChannel(),
      signalingGate: NullNotifySignalingGate.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      signalBus: NullSignalBus.Instance);

    // One consumer loop, so the first batch the worker reads is everything the claim loop handed it.
    var workerOptions = new PerspectiveWorkerOptions { MaxConcurrentDrainConsumers = 1, WriteMode = mode };
    var perspectiveWorker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: scopeFactory,
      options: Options.Create(workerOptions),
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
      failureChannel: failures,
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
      Failures = failures,
      HandedOff = handedOff,
    };
  }

  private sealed class Pipeline : IAsyncDisposable {
    public required ServiceProvider Services { get; init; }
    public required ServiceInstanceProvider InstanceProvider { get; init; }
    public required ClaimWorker ClaimWorker { get; init; }
    public required PerspectiveWorker PerspectiveWorker { get; init; }
    public required SignalingCompletionChannel Completion { get; init; }
    public required RecordingFailureChannel Failures { get; init; }
    public required HandedOffStreams HandedOff { get; init; }
    private bool _claiming;
    private bool _working;

    /// <summary>Registers this instance and the perspective's association with the event type.</summary>
    public async Task RegisterAsync(NpgsqlConnection conn, CancellationToken ct) {
      await using (var reg = conn.CreateCommand()) {
        reg.CommandText = """
          INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
          VALUES (@id, 'set-based-svc', 'set-based-host', 1, NOW(), NOW(), '{}'::jsonb)
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
        VALUES (gen_random_uuid(), @eventType, 'perspective', @target, 'set-based-svc', @eventType, NOW(), NOW())
        ON CONFLICT DO NOTHING
        """;
      assoc.Parameters.AddWithValue("eventType", eventType);
      assoc.Parameters.AddWithValue("target", TypeNameFormatter.GetPerspectiveName(typeof(OrderPerspective)));
      await assoc.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Starts the claim loop and waits until it has handed every one of the streams to the worker's queues.</summary>
    public async Task HandOffAsync(IReadOnlyCollection<Guid> streams, CancellationToken ct) {
      var handed = HandedOff.WaitForAsync(streams);
      _claiming = true;
      await ClaimWorker.StartAsync(ct);
      await handed.WaitAsync(_signalDeadline, ct);
    }

    /// <summary>Starts the worker, whose first batch is everything queued.</summary>
    public async Task StartWorkerAsync(CancellationToken ct) {
      _working = true;
      await PerspectiveWorker.StartAsync(ct);
      await PerspectiveWorker.StartupScanComplete.WaitAsync(_signalDeadline, ct);
    }

    /// <summary>Commits one order event on the stream and stamps it as the commit-order stamper would; returns its work row.</summary>
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
      await using var work = conn.CreateCommand();
      work.CommandText = "SELECT event_work_id FROM wh_perspective_events WHERE event_id = @id";
      work.Parameters.AddWithValue("id", eventId);
      return (Guid)(await work.ExecuteScalarAsync(ct))!;
    }

    public async ValueTask DisposeAsync() {
      if (_working) {
        await PerspectiveWorker.StopAsync(CancellationToken.None);
      }
      if (_claiming) {
        await ClaimWorker.StopAsync(CancellationToken.None);
      }
      await Services.DisposeAsync();
    }
  }

  /// <summary>Records the streams the claim loop handed to the worker's queues, and signals when a set of them is in.</summary>
  private sealed class HandedOffStreams {
    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _handed = [];
    private readonly List<(HashSet<Guid> Streams, TaskCompletionSource Done)> _waits = [];

    public Task WaitForAsync(IEnumerable<Guid> streams) {
      lock (_lock) {
        var wanted = streams.ToHashSet();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (wanted.IsSubsetOf(_handed)) {
          done.SetResult();
        } else {
          _waits.Add((wanted, done));
        }
        return done.Task;
      }
    }

    public void Add(Guid streamId) {
      List<TaskCompletionSource> ready;
      lock (_lock) {
        _handed.Add(streamId);
        ready = [.. _waits.Where(w => w.Streams.IsSubsetOf(_handed)).Select(w => w.Done)];
        _waits.RemoveAll(w => ready.Contains(w.Done));
      }
      foreach (var done in ready) {
        done.TrySetResult();
      }
    }
  }

  private sealed class CountingWorkChannel(IPerspectiveChannelWriter inner, HandedOffStreams handed) : IPerspectiveChannelWriter {
    public System.Threading.Channels.ChannelReader<PerspectiveWork> Reader => inner.Reader;

    public async ValueTask WriteAsync(PerspectiveWork work, CancellationToken ct = default) {
      await inner.WriteAsync(work, ct);
      handed.Add(work.StreamId);
    }

    public bool TryWrite(PerspectiveWork work) {
      var written = inner.TryWrite(work);
      if (written) {
        handed.Add(work.StreamId);
      }
      return written;
    }

    public void Complete() => inner.Complete();
  }

  private sealed class CountingDrainChannel(IPerspectiveDrainChannel inner, HandedOffStreams handed) : IPerspectiveDrainChannel {
    public System.Threading.Channels.ChannelReader<Guid> Reader => inner.Reader;

    public async ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) {
      await inner.WriteAsync(streamId, cancellationToken);
      handed.Add(streamId);
    }

    public bool TryWrite(Guid streamId) {
      var written = inner.TryWrite(streamId);
      if (written) {
        handed.Add(streamId);
      }
      return written;
    }

    public bool IsInFlight(Guid streamId) => inner.IsInFlight(streamId);
    public void MarkDraining(Guid streamId) => inner.MarkDraining(streamId);
    public void MarkDrained(Guid streamId) => inner.MarkDrained(streamId);
  }

  /// <summary>Completes each row and cursor at the coordinator before returning, then signals each completed row.</summary>
  private sealed class SignalingCompletionChannel(IServiceScopeFactory scopeFactory) : IPerspectiveCompletionChannel {
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _completed = new();

    public Task WaitForAllAsync(IEnumerable<Guid> workIds) =>
      Task.WhenAll(workIds.Select(id => _signal(id).Task)).WaitAsync(_signalDeadline);

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

  /// <summary>Signals each failed row, with what failed it.</summary>
  private sealed class RecordingFailureChannel(IServiceScopeFactory scopeFactory) : IFailureChannel {
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<MessageFailure>> _failed = new();

    public Task<MessageFailure> WaitForAsync(Guid workId) => _signal(workId).Task.WaitAsync(_signalDeadline);

    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      _ = scopeFactory;
      _signal(failure.MessageId).TrySetResult(failure);
      return ValueTask.CompletedTask;
    }

    private TaskCompletionSource<MessageFailure> _signal(Guid workId) =>
      _failed.GetOrAdd(workId, static _ => new TaskCompletionSource<MessageFailure>(TaskCreationOptions.RunContinuationsAsynchronously));
  }

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private static async Task<decimal> _amountAsync(NpgsqlConnection conn, Guid streamId, CancellationToken ct) {
    await using var command = conn.CreateCommand();
    command.CommandText = $"SELECT (data ->> 'Amount')::numeric FROM {TABLE} WHERE id = @id";
    command.Parameters.AddWithValue("id", streamId);
    return (decimal)(await command.ExecuteScalarAsync(ct))!;
  }

  private static async Task<bool> _rowExistsAsync(NpgsqlConnection conn, Guid streamId, CancellationToken ct) {
    await using var command = conn.CreateCommand();
    command.CommandText = $"SELECT EXISTS (SELECT 1 FROM {TABLE} WHERE id = @id)";
    command.Parameters.AddWithValue("id", streamId);
    return (bool)(await command.ExecuteScalarAsync(ct))!;
  }
}
