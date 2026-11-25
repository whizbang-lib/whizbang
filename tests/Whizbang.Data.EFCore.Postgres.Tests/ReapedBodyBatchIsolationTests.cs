// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
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
/// One stream whose event cannot be read fails only itself (#2591). The shape a deployed environment stalled on: an
/// ephemeral stream of 11 events written in July whose bodies the reap has deleted, its cursor rewound to version 1 and
/// that replay queued, sharing one perspective batch with healthy streams. The healthy streams are applied; the reaped
/// stream's row is failed with a named error, retried a bounded number of times, and dead-lettered.
/// </summary>
/// <remarks>
/// Real PostgreSQL and the real pieces throughout: <c>commit_handler_result</c> writes the healthy events, the real claim
/// loop leases every pending stream in one claim, <see cref="PerspectiveWorker"/> fetches the batch through
/// <c>get_stream_events_in_stream_order</c> and the generated <c>OrderPerspective</c> runner applies it. The completion
/// and failure channels write through to the coordinator before they return, so every wait is on the transition it
/// asserts. A retry becoming due is <c>scheduled_for</c> moved to the past, not a wait for the backoff.
/// </remarks>
/// <docs>operations/workers/perspective-worker#unreadable-stored-events</docs>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class ReapedBodyBatchIsolationTests : EFCoreTestBase {
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(60);
  private static readonly DateTimeOffset _writtenInJuly = new(2026, 7, 2, 9, 0, 0, TimeSpan.Zero);
  private const int EPHEMERAL = 8;
  private const int MAX_FAILURES = 1;
  private const int BATCH_LOST_EVENT_ID = 68;
  private const int REAPED_BODY_EVENT_ID = 84;
  private const int MISSING_BODY_EVENT_ID = 85;
  private const int DEAD_LETTER_RECORD_EVENT_ID = 1901;
  private static readonly LogLevel[] _oneWarning = [LogLevel.Warning];

  /// <summary>
  /// The deployed shape: an ephemeral stream whose bodies were reaped shares a batch with healthy streams. The healthy
  /// streams are applied, and the reaped row is dead-lettered on first sight with <see cref="MessageFailureReason.EventBodyMissing"/>:
  /// a reaped body never comes back, so no retry is spent on it.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task AReapedStreamSharingABatch_FailsOnlyItself_AndIsDeadLetteredOnFirstSightAsync(
      CancellationToken cancellationToken) {
    await using var pipeline = _composePipeline(maxFailures: 10, RewindStartupMode.Background);
    await using var conn = await _openAsync(cancellationToken);
    var run = await _runASharedBatchAsync(pipeline, conn, EPHEMERAL, cancellationToken);

    await pipeline.DeadLetters.WaitAsync(items => items.Contains(run.ReapedWork), cancellationToken);
    await Assert.That(pipeline.Failures.Snapshot().Where(f => f.MessageId == run.ReapedWork)).IsEmpty()
      .Because("a reaped body never comes back, so the row is not failed for a retry first");
    await Assert.That(await _deadLetterReasonAsync(conn, run.ReapedWork, cancellationToken))
      .IsEqualTo((int)MessageFailureReason.EventBodyMissing);
    await Assert.That(await _pendingRowExistsAsync(conn, run.ReapedWork, cancellationToken)).IsFalse()
      .Because("the dead letter takes the row out of the work table, so no batch leases it again");
    var named = _linesNaming(pipeline, run);
    await Assert.That(named).IsNotEmpty().Because("the log names the stream, the perspective and the error");
    await Assert.That(named.Where(l => l.EventId == REAPED_BODY_EVENT_ID).Select(l => l.Level)).IsEquivalentTo(_oneWarning)
      .Because("a reaped ephemeral body is the retention working as configured, not a defect");
    await Assert.That(named.Any(l => l.EventId == MISSING_BODY_EVENT_ID)).IsFalse()
      .Because("the Error line is for a body that should never have been deleted");
    await Assert.That(named.Count(l => l.EventId == DEAD_LETTER_RECORD_EVENT_ID)).IsEqualTo(1)
      .Because("the one dead-letter record, at the level DeadLetterLogging configures, as for every dead letter");
  }

  /// <summary>
  /// The same shape with a Sourced event whose body is missing: that should never happen, so it is logged at Error, and
  /// the row keeps the bounded retry (a body restored in the meantime is read) before it is dead-lettered.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task AStreamMissingASourcedBody_FailsOnlyItself_IsRetriedBoundedly_AndIsDeadLetteredAtErrorAsync(
      CancellationToken cancellationToken) {
    await using var pipeline = _composePipeline(MAX_FAILURES, RewindStartupMode.Background);
    await using var conn = await _openAsync(cancellationToken);
    var run = await _runASharedBatchAsync(pipeline, conn, flags: 0, cancellationToken);

    // The row is failed with a named error: the database records it, and the row retries with backoff.
    await pipeline.Failures.WaitAsync(items => items.Any(f => f.MessageId == run.ReapedWork), cancellationToken);
    var failure = pipeline.Failures.Snapshot().First(f => f.MessageId == run.ReapedWork);
    await Assert.That(failure.Error).Contains(run.ReapedStream.ToString()).And.Contains("body")
      .Because("the error names the stream and what is missing");
    await Assert.That(failure.Reason).IsEqualTo(MessageFailureReason.EventBodyMissing);
    await Assert.That(await _failuresAsync(conn, run.ReapedWork, cancellationToken)).IsGreaterThanOrEqualTo(1)
      .Because("the failure is counted against the row, which the dead-letter bound reads");
    var named = _linesNaming(pipeline, run);
    await Assert.That(named).IsNotEmpty().Because("the log names the stream, the perspective and the error");
    await Assert.That(named.Any(l => l.Level == LogLevel.Error)).IsTrue()
      .Because("a Sourced body is never reaped, so its absence means something else is wrong");

    // Each retry made due fails again, until the bound moves the row to the dead letters.
    for (var attempt = 0; attempt <= MAX_FAILURES + 1 && !pipeline.DeadLetters.Snapshot().Contains(run.ReapedWork); attempt++) {
      var next = pipeline.Failures.WaitAsync(items => items.Any(f => f.MessageId == run.ReapedWork), cancellationToken, fromNow: true);
      var moved = pipeline.DeadLetters.WaitAsync(items => items.Contains(run.ReapedWork), cancellationToken);
      await _makeRetryDueAsync(conn, run.ReapedWork, cancellationToken);
      await Task.WhenAny(next, moved);
    }
    await pipeline.DeadLetters.WaitAsync(items => items.Contains(run.ReapedWork), cancellationToken);
    await Assert.That(pipeline.Failures.Snapshot().Count(f => f.MessageId == run.ReapedWork)).IsLessThanOrEqualTo(MAX_FAILURES + 1)
      .Because("the stream is retried a bounded number of times");
    await Assert.That(await _deadLetterExistsAsync(conn, run.ReapedWork, cancellationToken)).IsTrue();
    await Assert.That(await _pendingRowExistsAsync(conn, run.ReapedWork, cancellationToken)).IsFalse();
  }

  /// <summary>
  /// The database a deployed environment holds, repaired by migration 600: a Completed cursor at version 11 flagged to
  /// rewind from version 1, every one of the 11 ephemeral bodies reaped, and the version 1 replay queued. The repair
  /// clears the flag (the rewind can never complete) and deletes the queued row, so a worker starting in Blocking mode
  /// is not held behind a rewind that cannot finish.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task Migration600_ClearsARewindThatCanNeverComplete_AndABlockingStartupIsNotHeldAsync(
      CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var perspective = TypeNameFormatter.GetPerspectiveName(typeof(OrderPerspective));
    var stream = (Guid)TrackedGuid.New();
    var queued = await _seedReapedStreamRewoundToItsStartAsync(conn, stream, perspective, EPHEMERAL, cancellationToken);
    // The repair runs once per database; the template this database was copied from already ran it.
    await _execAsync(conn, "DELETE FROM wh_settings WHERE setting_key = 'rewind_reap_floor_repaired'", cancellationToken);

    await _execAsync(conn, _migrationSql("600_"), cancellationToken);

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        SELECT status, rewind_trigger_event_id IS NULL, rewind_flagged_at IS NULL, rewind_first_flagged_at IS NULL
        FROM wh_perspective_cursors WHERE stream_id = @sid AND perspective_name = @p
        """;
      cmd.Parameters.AddWithValue("sid", stream);
      cmd.Parameters.AddWithValue("p", perspective);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      await Assert.That(await reader.ReadAsync(cancellationToken)).IsTrue();
      await Assert.That(reader.GetInt32(0)).IsEqualTo(2).Because("Completed, with the RewindRequired bit cleared");
      await Assert.That(reader.GetBoolean(1) && reader.GetBoolean(2) && reader.GetBoolean(3)).IsTrue()
        .Because("the trigger and both flag times are cleared with the flag");
    }
    await Assert.That(await _pendingRowExistsAsync(conn, queued, cancellationToken)).IsFalse()
      .Because("the queued replay of an event without a body is deleted");

    await using var pipeline = _composePipeline(maxFailures: 10, RewindStartupMode.Blocking);
    await pipeline.RegisterAsync(conn, cancellationToken);
    await pipeline.StartAsync(cancellationToken);
    await Assert.That(pipeline.PerspectiveWorker.StartupScanComplete.IsCompletedSuccessfully).IsTrue()
      .Because("no cursor is left flagged for a rewind that can never complete, so the startup barrier opens");
  }

  // ===== The shared batch =====

  private sealed record SharedBatchRun(Guid ReapedStream, Guid ReapedWork, string Perspective);

  /// <summary>
  /// Seeds the stream without bodies (ephemeral or not, per <paramref name="flags"/>) rewound to its start, commits two
  /// healthy streams, starts the pipeline and waits until the healthy streams are applied in the batch they shared with
  /// it; a lost batch ends the test with what lost it.
  /// </summary>
  private static async Task<SharedBatchRun> _runASharedBatchAsync(Pipeline pipeline, NpgsqlConnection conn, int flags,
      CancellationToken cancellationToken) {
    await pipeline.RegisterAsync(conn, cancellationToken);
    var perspective = TypeNameFormatter.GetPerspectiveName(typeof(OrderPerspective));
    var reapedStream = (Guid)TrackedGuid.New();
    var reapedWork = await _seedReapedStreamRewoundToItsStartAsync(conn, reapedStream, perspective, flags, cancellationToken);
    var healthyA = (Guid)TrackedGuid.New();
    var healthyB = (Guid)TrackedGuid.New();
    var healthyEvents = await pipeline.CommitAsync(conn, [(healthyA, 10m), (healthyB, 20m)], cancellationToken);
    var healthyWork = new List<Guid>();
    foreach (var eventId in healthyEvents) {
      healthyWork.Add(await _workIdForEventAsync(conn, eventId, cancellationToken));
    }

    await pipeline.StartAsync(cancellationToken);

    // The healthy streams are applied, unless the batch is lost first: then the test ends with what lost it.
    var applied = pipeline.Completed.WaitAsync(items => healthyWork.All(items.Contains), cancellationToken);
    var lost = pipeline.Logs.WaitAsync(items => items.Any(l => l.EventId == BATCH_LOST_EVENT_ID), cancellationToken);
    var first = await Task.WhenAny(applied, lost);
    if (first == lost && lost.IsCompletedSuccessfully) {
      var record = pipeline.Logs.Snapshot().First(l => l.EventId == BATCH_LOST_EVENT_ID);
      Assert.Fail($"the whole batch was lost to the stream without bodies: {record.Message} {record.Exception}");
    }
    await applied;
    await Assert.That(pipeline.Claims.Snapshot().Any(c => c.Contains(reapedStream) && c.Contains(healthyA) && c.Contains(healthyB)))
      .IsTrue().Because("the stream without bodies and the healthy ones were leased together, in one batch");
    await Assert.That(await _readAmountAsync(conn, healthyA, cancellationToken)).IsEqualTo(10m);
    await Assert.That(await _readAmountAsync(conn, healthyB, cancellationToken)).IsEqualTo(20m);
    return new SharedBatchRun(reapedStream, reapedWork, perspective);
  }

  private static List<LogRecord> _linesNaming(Pipeline pipeline, SharedBatchRun run) =>
    [.. pipeline.Logs.Snapshot().Where(l => l.Level >= LogLevel.Warning
      && l.Message.Contains(run.ReapedStream.ToString(), StringComparison.Ordinal)
      && l.Message.Contains(run.Perspective, StringComparison.Ordinal)
      && l.Message.Contains("body", StringComparison.OrdinalIgnoreCase))];

  // ===== Seeding =====

  /// <summary>
  /// The reaped stream as the deployed database held it: 11 ephemeral events of the perspective's type at versions 1 to
  /// 11, stamped, written in July, with no body row; the cursor at version 11, flagged to rewind from version 1 with no
  /// snapshot left; and that replay queued as a pending, unattempted row. Returns the queued row's work id.
  /// </summary>
  private static async Task<Guid> _seedReapedStreamRewoundToItsStartAsync(
      NpgsqlConnection conn, Guid streamId, string perspective, int flags, CancellationToken ct) {
    var eventType = TypeNameFormatter.Format(typeof(SampleOrderCreatedEvent));
    var ids = new List<Guid>();
    for (var v = 1; v <= 11; v++) {
      var id = (Guid)TrackedGuid.New();
      ids.Add(id);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = """
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at,
                                    commit_sequence, flags)
        VALUES (@id, @sid, @sid, 'Order', @v, @type, NULL, @written, nextval('wh_commit_seq'), @flags)
        """;
      cmd.Parameters.AddWithValue("id", id);
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("v", v);
      cmd.Parameters.AddWithValue("type", eventType);
      cmd.Parameters.AddWithValue("written", _writtenInJuly);
      cmd.Parameters.AddWithValue(nameof(flags), flags);
      await cmd.ExecuteNonQueryAsync(ct);
    }
    var workId = (Guid)TrackedGuid.New();
    await using var rewind = conn.CreateCommand();
    rewind.CommandText = """
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, rewind_trigger_event_id,
                                          rewind_flagged_at, rewind_first_flagged_at)
      VALUES (@sid, @perspective, @last, 2 | 32, @first, @written, @written);
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
      VALUES (@work, @sid, @perspective, @first, compute_partition(@sid, 10000), 1, 0, @written);
      """;
    rewind.Parameters.AddWithValue("sid", streamId);
    rewind.Parameters.AddWithValue(nameof(perspective), perspective);
    rewind.Parameters.AddWithValue("last", ids[^1]);
    rewind.Parameters.AddWithValue("first", ids[0]);
    rewind.Parameters.AddWithValue("written", _writtenInJuly);
    rewind.Parameters.AddWithValue("work", workId);
    await rewind.ExecuteNonQueryAsync(ct);
    return workId;
  }

  // ===== Pipeline =====

  private Pipeline _composePipeline(int maxFailures, RewindStartupMode startupMode) {
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
    services.AddMetrics();
    var provider = services.BuildServiceProvider();
    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

    var instanceProvider = new ServiceInstanceProvider(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    var harness = new PerspectiveWorkerTestHarness();
    var claims = new Recorder<HashSet<Guid>>();
    var failures = new Recorder<MessageFailure>();
    var completed = new Recorder<Guid>();
    var deadLetters = new Recorder<Guid>();
    var logs = new Recorder<LogRecord>();

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
      perspectiveChannel: harness.ChannelWriter,
      perspectiveDrainChannel: harness.DrainChannel,
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: new InboxDrainChannel(),
      signalingGate: NullNotifySignalingGate.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      signalBus: NullSignalBus.Instance);
    claimWorker.OnBatchClaimed += batch =>
      claims.Add([.. batch.PerspectiveStreamIds, .. batch.PerspectiveWork.Select(w => w.StreamId)]);

    var workerOptions = new PerspectiveWorkerOptions {
      MaxPerspectiveEventAttempts = maxFailures,
      MaxConcurrentDrainConsumers = 1,
    };
    var perspectiveWorker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: scopeFactory,
      options: Options.Create(workerOptions),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: provider.GetRequiredService<IEventTypeProvider>(),
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: new RecordingLogger<PerspectiveWorker>(logs),
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      // A test whose stream is flagged when the worker starts runs in Background mode: the deployed worker was already
      // running when the flag was raised, so the startup barrier did not wait on it there either.
      rewindOptions: Options.Create(new PerspectiveRewindOptions { StartupRewindMode = startupMode }),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: new WriteThroughCompletionChannel(scopeFactory, completed),
      failureChannel: new WriteThroughFailureChannel(scopeFactory, failures),
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: new RecordingDeadLetterStore(scopeFactory, deadLetters),
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor(workerOptions));

    return new Pipeline {
      Services = provider,
      InstanceProvider = instanceProvider,
      ClaimWorker = claimWorker,
      PerspectiveWorker = perspectiveWorker,
      Claims = claims,
      Failures = failures,
      Completed = completed,
      DeadLetters = deadLetters,
      Logs = logs,
    };
  }

  private sealed class Pipeline : IAsyncDisposable {
    public required ServiceProvider Services { get; init; }
    public required ServiceInstanceProvider InstanceProvider { get; init; }
    public required ClaimWorker ClaimWorker { get; init; }
    public required PerspectiveWorker PerspectiveWorker { get; init; }
    public required Recorder<HashSet<Guid>> Claims { get; init; }
    public required Recorder<MessageFailure> Failures { get; init; }
    public required Recorder<Guid> Completed { get; init; }
    public required Recorder<Guid> DeadLetters { get; init; }
    public required Recorder<LogRecord> Logs { get; init; }
    private bool _started;

    /// <summary>Registers this instance and the perspective's association with the event type.</summary>
    public async Task RegisterAsync(NpgsqlConnection conn, CancellationToken ct) {
      await using (var reg = conn.CreateCommand()) {
        reg.CommandText = """
          INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
          VALUES (@id, 'reaped-body-svc', 'reaped-body-host', 1, NOW(), NOW(), '{}'::jsonb)
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
        VALUES (gen_random_uuid(), @eventType, 'perspective', @target, 'reaped-body-svc', @eventType, NOW(), NOW())
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

    /// <summary>Commits one order event per entry, each on its stream, stamps them, and returns the event ids in order.</summary>
    public async Task<List<Guid>> CommitAsync(NpgsqlConnection conn, IReadOnlyList<(Guid StreamId, decimal Amount)> events, CancellationToken ct) {
      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      var ids = new List<Guid>();
      var messages = new List<string>();
      foreach (var (streamId, amount) in events) {
        var eventId = (Guid)TrackedGuid.New();
        ids.Add(eventId);
        var payload = new SampleOrderCreatedEvent { OrderId = new TestOrderId(streamId), Amount = amount };
        var envelope = new MessageEnvelope<JsonElement> {
          MessageId = MessageId.From(eventId),
          DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
          Hops = [],
          Payload = JsonSerializer.SerializeToElement(payload, jsonOptions.GetTypeInfo(typeof(SampleOrderCreatedEvent))),
        };
        var envelopeJson = JsonSerializer.Serialize(envelope, jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
        messages.Add($$"""
          {
            "MessageId": "{{eventId}}",
            "Destination": "order-events",
            "MessageType": "{{TypeNameFormatter.Format(typeof(SampleOrderCreatedEvent))}}",
            "EnvelopeType": null,
            "Envelope": {{envelopeJson}},
            "Metadata": {},
            "Scope": null,
            "StreamId": "{{streamId}}",
            "IsEvent": true
          }
          """);
      }
      var request = $$"""
        {
          "instance_id": "{{InstanceProvider.InstanceId}}",
          "service_name": "{{InstanceProvider.ServiceName}}",
          "host_name": "{{InstanceProvider.HostName}}",
          "process_id": {{InstanceProvider.ProcessId}},
          "new_outbox_messages": [{{string.Join(",", messages)}}]
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
      return ids;
    }

    public async ValueTask DisposeAsync() {
      if (_started) {
        await PerspectiveWorker.StopAsync(CancellationToken.None);
        await ClaimWorker.StopAsync(CancellationToken.None);
      }
      await Services.DisposeAsync();
    }
  }

  /// <summary>Reports each failure to the coordinator before returning, then records it.</summary>
  private sealed class WriteThroughFailureChannel(IServiceScopeFactory scopeFactory, Recorder<MessageFailure> recorded) : IFailureChannel {
    public async ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      await using (var scope = scopeFactory.CreateAsyncScope()) {
        await scope.ServiceProvider.GetRequiredService<IWorkCoordinator>()
          .ReportFailuresAsync(category, [failure], cancellationToken);
      }
      recorded.Add(failure);
    }
  }

  /// <summary>Completes each row and cursor at the coordinator before returning, then records the rows.</summary>
  private sealed class WriteThroughCompletionChannel(IServiceScopeFactory scopeFactory, Recorder<Guid> completed) : IPerspectiveCompletionChannel {
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
        completed.Add(workId);
      }
    }
  }

  /// <summary>The Entity Framework dead-letter store, one context per move, recording what it moved.</summary>
  private sealed class RecordingDeadLetterStore(IServiceScopeFactory scopeFactory, Recorder<Guid> moved) : IDeadLetterStore {
    public async Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId, MessageFailureReason failureReason,
        string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      await using var scope = scopeFactory.CreateAsyncScope();
      var store = new EFCoreDeadLetterStore<WorkCoordinationDbContext>(
        scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>());
      var result = await store.MoveAsync(deadLetterId, sourceTable, sourceId, failureReason, errorText, instanceId, generation, ct);
      moved.Add(sourceId);
      return result;
    }
  }

  private sealed record LogRecord(LogLevel Level, int EventId, string Message, Exception? Exception);

  private sealed class RecordingLogger<T>(Recorder<LogRecord> records) : ILogger<T> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      records.Add(new LogRecord(logLevel, eventId.Id, formatter(state, exception), exception));
  }

  /// <summary>
  /// An append-only record that a test can wait on: a wait completes the moment the recorded items satisfy its condition,
  /// so nothing is polled and nothing sleeps.
  /// </summary>
  private sealed class Recorder<T> {
    private readonly Lock _lock = new();
    private readonly List<T> _items = [];
    private readonly List<(int From, Func<IReadOnlyList<T>, bool> Done, TaskCompletionSource Signal)> _waiters = [];

    public void Add(T item) {
      lock (_lock) {
        _items.Add(item);
        for (var i = _waiters.Count - 1; i >= 0; i--) {
          var (from, done, signal) = _waiters[i];
          if (done(_items.GetRange(from, _items.Count - from))) {
            signal.TrySetResult();
            _waiters.RemoveAt(i);
          }
        }
      }
    }

    public List<T> Snapshot() {
      lock (_lock) {
        return [.. _items];
      }
    }

    /// <summary>
    /// Completes when the items recorded (from now on, when <paramref name="fromNow"/>) satisfy <paramref name="done"/>;
    /// fails the test if that has not happened by the deadline.
    /// </summary>
    public Task WaitAsync(Func<IReadOnlyList<T>, bool> done, CancellationToken ct, bool fromNow = false) {
      var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      lock (_lock) {
        var from = fromNow ? _items.Count : 0;
        if (done(_items.GetRange(from, _items.Count - from))) {
          return Task.CompletedTask;
        }
        _waiters.Add((from, done, signal));
      }
      return signal.Task.WaitAsync(_signalDeadline, ct);
    }
  }

  // ===== SQL helpers =====

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private static async Task<Guid> _workIdForEventAsync(NpgsqlConnection conn, Guid eventId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_work_id FROM wh_perspective_events WHERE event_id = @eid";
    cmd.Parameters.AddWithValue("eid", eventId);
    return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task<int> _failuresAsync(NpgsqlConnection conn, Guid workId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COALESCE((SELECT failures FROM wh_perspective_events WHERE event_work_id = @work), -1)";
    cmd.Parameters.AddWithValue("work", workId);
    return (int)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task _makeRetryDueAsync(NpgsqlConnection conn, Guid workId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_perspective_events SET scheduled_for = NOW() - INTERVAL '1 second' WHERE event_work_id = @work";
    cmd.Parameters.AddWithValue("work", workId);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task<bool> _pendingRowExistsAsync(NpgsqlConnection conn, Guid workId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM wh_perspective_events WHERE event_work_id = @work AND processed_at IS NULL)";
    cmd.Parameters.AddWithValue("work", workId);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task<int> _deadLetterReasonAsync(NpgsqlConnection conn, Guid workId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT failure_reason FROM wh_dead_letters WHERE source_id = @work";
    cmd.Parameters.AddWithValue("work", workId);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static string _migrationSql(string prefix) =>
    new PostgresMigrationProvider().GetMigrations().Single(m => m.Name.StartsWith(prefix, StringComparison.Ordinal)).Sql;

  private static async Task<bool> _deadLetterExistsAsync(NpgsqlConnection conn, Guid workId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM wh_dead_letters WHERE source_id = @work)";
    cmd.Parameters.AddWithValue("work", workId);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task<decimal> _readAmountAsync(NpgsqlConnection conn, Guid streamId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT (data ->> 'Amount')::numeric FROM wh_per_order WHERE id = @id";
    cmd.Parameters.AddWithValue("id", streamId);
    return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
  }
}
