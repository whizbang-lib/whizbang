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

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A stream whose late copies sit after its terminal event (#2366): the terminal event at version 1, then three older
/// copies of an earlier event at versions 2 to 4 with later commit sequences, the shape a redelivered copy that lands
/// above the newest leaves. Replaying such a stream reproduces the same out-of-order input, so a rewind for it ends where
/// it started. It must be settled once, never scheduled again.
/// </summary>
/// <remarks>
/// Real PostgreSQL and the real pieces: <c>commit_handler_result</c> writes the events and their work rows, the real claim
/// loop, <see cref="PerspectiveWorker"/> and generated <c>OrderPerspective</c> runner apply them to <c>wh_per_order</c>.
/// Every wait is on a signal: a work row's completion, and the claim loop's own polls, counted.
/// </remarks>
/// <docs>fundamentals/perspectives/rewind#a-rewind-that-changes-nothing-is-settled</docs>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class PerspectiveRewindLoopTests : EFCoreTestBase {
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(60);

  /// <summary>Claim polls watched after the stream settles: a loop re-claims it on the next one or two.</summary>
  private const int QUIET_POLLS = 30;

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task AStreamWithOlderCopiesAfterItsTerminalEvent_IsRewoundOnce_AndNeverScheduledAgainAsync(bool snapshots) {
    var ct = CancellationToken.None;
    var streamId = (Guid)TrackedGuid.New();
    var rewinds = new RewindCountingLogger();
    await using var pipeline = _composePipeline(rewinds, snapshots);
    await using var conn = await _openAsync(ct);
    await pipeline.RegisterAsync(conn, ct);

    // The copies belong before the terminal event: their ids are older. They land after it.
    var minted = DateTimeOffset.UtcNow.AddHours(-2);
    var copies = new[] {
      Guid.CreateVersion7(minted), Guid.CreateVersion7(minted.AddSeconds(1)), Guid.CreateVersion7(minted.AddSeconds(2)),
    };
    var terminal = Guid.CreateVersion7(minted.AddHours(1));

    // The stream as a consumer holds it: the terminal event received first (version 1), the copies stored after it
    // (versions 2 to 4, later commit sequences), all from one origin whose order puts the copies first. The terminal
    // event was applied, and storing the copies below the cursor flagged it for a rewind with the earliest as trigger,
    // as the receive chain does.
    await pipeline.CommitAsync(conn, streamId, [(terminal, 100m)], ct);
    await pipeline.CommitAsync(conn, streamId, [.. copies.Select((id, i) => (id, (decimal)(i + 1)))], ct);
    await _receivedFromOneOriginAsync(conn, streamId, terminal, copies, ct);
    await _appliedAndFlaggedAsync(conn, streamId, terminal, copies[0], ct);

    // Read before the worker starts: a completed row is deleted, so a lookup after the start can find nothing.
    var workIds = new List<Guid>();
    foreach (var copy in copies) {
      workIds.Add(await _workIdAsync(conn, copy, ct));
    }
    await pipeline.StartAsync(ct);
    foreach (var workId in workIds) {
      await pipeline.CompletionOf(workId).WaitAsync(_signalDeadline, ct);
    }
    var rewindsWhenSettled = rewinds.Count;

    var reclaims = 0;
    var polls = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var seen = 0;
    pipeline.ClaimWorker.OnBatchClaimed += batch => {
      if (batch.PerspectiveStreamIds.Contains(streamId) || batch.PerspectiveWork.Any(w => w.StreamId == streamId)) {
        Interlocked.Increment(ref reclaims);
      }
      if (Interlocked.Increment(ref seen) >= QUIET_POLLS) {
        polls.TrySetResult();
      }
    };
    await polls.Task.WaitAsync(_signalDeadline, ct);

    var flagged = await _scalarAsync<bool>(conn,
      "SELECT COALESCE(bool_or((status & 32) = 32), false) FROM wh_perspective_cursors WHERE stream_id = @id", streamId, ct);
    Console.WriteLine($"snapshots={snapshots} rewinds when settled={rewindsWhenSettled} after={rewinds.Count} reclaims={reclaims} flagged={flagged}");

    await Assert.That(reclaims).IsEqualTo(0)
      .Because($"a rewind that ends where it started is settled; the stream was claimed again {reclaims} times in {QUIET_POLLS} polls");
    await Assert.That(rewinds.Count).IsEqualTo(rewindsWhenSettled).Because("no second rewind is scheduled");
    await Assert.That(flagged).IsFalse().Because("the cursor holds no rewind request once the rewind settled");
  }

  /// <summary>
  /// The stuck shape exactly (#2366): the cursor on the stream's highest event id, which sits at its lowest version;
  /// the earliest copy (the flag's trigger) already retired; the two later copies, lower ids at higher versions,
  /// pending; status RewindRequired | Completed. At most one rewind settles it; then a copy queued again (a redelivery
  /// under a new work row) is already applied, so it is completed with no second rewind,
  /// whether the same worker or a restarted one takes it. The cursor ends on the stream's last version, in the order
  /// the replay folds, and the flag is clear.
  /// </summary>
  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task AStuckCursorOnTheHighestIdAtTheLowestVersion_ConvergesAfterOneRewind_AndARequeuedCopyIsNotRewoundAsync(bool restart) {
    var ct = CancellationToken.None;
    var streamId = (Guid)TrackedGuid.New();
    var rewinds = new RewindCountingLogger();
    var pipeline = _composePipeline(rewinds, snapshots: true);
    try {
      await using var conn = await _openAsync(ct);
      await pipeline.RegisterAsync(conn, ct);

      var minted = DateTimeOffset.UtcNow.AddHours(-2);
      var copies = new[] {
        Guid.CreateVersion7(minted), Guid.CreateVersion7(minted.AddSeconds(1)), Guid.CreateVersion7(minted.AddSeconds(2)),
      };
      var terminal = Guid.CreateVersion7(minted.AddHours(1));
      await pipeline.CommitAsync(conn, streamId, [(terminal, 100m)], ct);
      await pipeline.CommitAsync(conn, streamId, [.. copies.Select((id, i) => (id, (decimal)(i + 1)))], ct);
      await _receivedFromOneOriginAsync(conn, streamId, terminal, copies, ct);
      await _appliedAndFlaggedAsync(conn, streamId, terminal, copies[0], ct);
      // The trigger's own row was retired by an earlier pass; only the two later copies are pending.
      await _executeAsync(conn, "DELETE FROM wh_perspective_events WHERE event_id = @id", copies[0], ct);

      // Read before the worker starts: a completed row is deleted, so a lookup after the start can find nothing.
      var workIds = new List<Guid>();
      foreach (var copy in copies[1..]) {
        workIds.Add(await _workIdAsync(conn, copy, ct));
      }
      await pipeline.StartAsync(ct);
      foreach (var workId in workIds) {
        await pipeline.CompletionOf(workId).WaitAsync(_signalDeadline, ct);
      }
      var rewindsWhenSettled = rewinds.Count;
      Console.WriteLine($"restart={restart} settled: rewinds={rewindsWhenSettled} {await _cursorAsync(conn, streamId, ct)}");

      if (restart) {
        await pipeline.DisposeAsync();
        pipeline = _composePipeline(rewinds, snapshots: true);
        await pipeline.StartAsync(ct);
      }

      // A redelivered copy of the middle event, queued again under a new work row.
      var requeued = await _requeueAsync(conn, streamId, copies[1], ct);
      await pipeline.CompletionOf(requeued).WaitAsync(_signalDeadline, ct);
      Console.WriteLine($"restart={restart} requeued: rewinds={rewinds.Count} {await _cursorAsync(conn, streamId, ct)}");

      var cursorVersion = await _scalarAsync<long>(conn, """
        SELECT e.version::bigint FROM wh_perspective_cursors c JOIN wh_event_store e ON e.event_id = c.last_event_id
        WHERE c.stream_id = @id
        """, streamId, ct);
      var flagged = await _scalarAsync<bool>(conn,
        "SELECT COALESCE(bool_or((status & 32) = 32), false) FROM wh_perspective_cursors WHERE stream_id = @id", streamId, ct);

      await Assert.That(rewindsWhenSettled).IsLessThanOrEqualTo(1).Because("the stuck cursor is settled by at most one rewind");
      await Assert.That(rewinds.Count).IsEqualTo(rewindsWhenSettled)
        .Because("a copy already applied, queued again, is completed without a second rewind");
      await Assert.That(cursorVersion).IsEqualTo(4L)
        .Because("the cursor ends on the last event in the order the replay folds the stream, not on the highest id");
      await Assert.That(flagged).IsFalse().Because("the completed rewind clears its flag");
    } finally {
      await pipeline.DisposeAsync();
    }
  }

  // ===== Helpers =====

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, Guid id, CancellationToken ct) {
    await using var command = conn.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue(nameof(id), id);
    await command.ExecuteNonQueryAsync(ct);
  }

  /// <summary>Queues a stored event again for the perspective under a new work row, as a redelivery does.</summary>
  private static async Task<Guid> _requeueAsync(NpgsqlConnection conn, Guid streamId, Guid eventId, CancellationToken ct) {
    var workId = (Guid)TrackedGuid.New();
    await using var command = conn.CreateCommand();
    command.CommandText = """
      INSERT INTO wh_perspective_events (event_work_id, event_id, stream_id, perspective_name, status, attempts, created_at)
      VALUES (@work, @event, @stream, @perspective, 0, 0, NOW())
      """;
    command.Parameters.AddWithValue("work", workId);
    command.Parameters.AddWithValue("event", eventId);
    command.Parameters.AddWithValue("stream", streamId);
    command.Parameters.AddWithValue("perspective", TypeNameFormatter.GetPerspectiveName(typeof(OrderPerspective)));
    await command.ExecuteNonQueryAsync(ct);
    return workId;
  }

  private static async Task<string> _cursorAsync(NpgsqlConnection conn, Guid streamId, CancellationToken ct) {
    await using var command = conn.CreateCommand();
    command.CommandText = """
      SELECT format('cursor v%s status %s trigger %s pending %s', e.version, c.status, c.rewind_trigger_event_id,
        (SELECT count(*) FROM wh_perspective_events pe WHERE pe.stream_id = c.stream_id AND pe.processed_at IS NULL))
      FROM wh_perspective_cursors c JOIN wh_event_store e ON e.event_id = c.last_event_id WHERE c.stream_id = @id
      """;
    command.Parameters.AddWithValue("id", streamId);
    return (string?)await command.ExecuteScalarAsync(ct) ?? "no cursor";
  }

  private static readonly Guid _origin = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-000000002366");

  /// <summary>Stamps the four events as received from one origin, whose order is the copies first, then the terminal.</summary>
  private static async Task _receivedFromOneOriginAsync(
      NpgsqlConnection conn, Guid streamId, Guid terminal, Guid[] copies, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      UPDATE wh_event_store
      SET origin_service_id = @origin,
          origin_commit_sequence = CASE WHEN event_id = @terminal THEN 100 ELSE array_position(@copies, event_id) END
      WHERE stream_id = @stream
      """;
    cmd.Parameters.AddWithValue("origin", _origin);
    cmd.Parameters.AddWithValue(nameof(terminal), terminal);
    cmd.Parameters.AddWithValue(nameof(copies), copies);
    cmd.Parameters.AddWithValue("stream", streamId);
    await Assert.That(await cmd.ExecuteNonQueryAsync(ct)).IsEqualTo(4);
  }

  /// <summary>
  /// The terminal event applied (its work row gone, the cursor on it) and the cursor flagged RewindRequired with the
  /// earliest copy as trigger, as the receive chain flags a stored event below the cursor.
  /// </summary>
  private static async Task _appliedAndFlaggedAsync(
      NpgsqlConnection conn, Guid streamId, Guid terminal, Guid trigger, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      DELETE FROM wh_perspective_events WHERE event_id = @terminal;
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at,
                                          rewind_trigger_event_id, rewind_flagged_at, rewind_first_flagged_at)
      VALUES (@stream, @perspective, @terminal, 2 | 32, NOW(), @trigger, NOW(), NOW())
      ON CONFLICT (stream_id, perspective_name) DO UPDATE
        SET last_event_id = EXCLUDED.last_event_id, status = EXCLUDED.status,
            rewind_trigger_event_id = EXCLUDED.rewind_trigger_event_id, rewind_flagged_at = EXCLUDED.rewind_flagged_at,
            rewind_first_flagged_at = EXCLUDED.rewind_first_flagged_at;
      """;
    cmd.Parameters.AddWithValue(nameof(terminal), terminal);
    cmd.Parameters.AddWithValue(nameof(trigger), trigger);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("perspective", TypeNameFormatter.GetPerspectiveName(typeof(OrderPerspective)));
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static Task<Guid> _workIdAsync(NpgsqlConnection conn, Guid eventId, CancellationToken ct) =>
    _scalarAsync<Guid>(conn, "SELECT event_work_id FROM wh_perspective_events WHERE event_id = @id", eventId, ct);

  /// <summary>Counts the worker's rewind decisions: an inversion found in a drain, or a cursor flagged for one.</summary>
  private sealed class RewindCountingLogger : Microsoft.Extensions.Logging.ILogger<PerspectiveWorker> {
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      if (message.Contains("triggering rewind", StringComparison.Ordinal)
          || message.StartsWith("Perspective rewind required", StringComparison.Ordinal)) {
        Interlocked.Increment(ref _count);
      }
    }
  }

  private Pipeline _composePipeline(RewindCountingLogger rewinds, bool snapshots) {
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
    if (snapshots) {
      // A rewind that applied something snapshots its result (#1002), and the next rewind starts from a snapshot.
      services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));
      services.AddSingleton<IPerspectiveSnapshotStore>(sp => new EFCorePerspectiveSnapshotStore(sp.GetRequiredService<NpgsqlDataSource>()));
      services.Configure<PerspectiveSnapshotOptions>(o => o.Enabled = true);
    }
    var provider = services.BuildServiceProvider();
    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

    var instanceProvider = new ServiceInstanceProvider(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    var harness = new PerspectiveWorkerTestHarness();
    var completion = new SignalingCompletionChannel(scopeFactory);

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

    var workerOptions = new PerspectiveWorkerOptions { MaxConcurrentDrainConsumers = 1 };
    var perspectiveWorker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: scopeFactory,
      options: Options.Create(workerOptions),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: provider.GetRequiredService<IEventTypeProvider>(),
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: rewinds,
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
          VALUES (@id, 'rewind-loop-svc', 'rewind-loop-host', 1, NOW(), NOW(), '{}'::jsonb)
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
        VALUES (gen_random_uuid(), @eventType, 'perspective', @target, 'rewind-loop-svc', @eventType, NOW(), NOW())
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

    /// <summary>
    /// Commits the events in one handler result, in the order given, each with its own id and amount, and stamps them as
    /// the commit-order stamper would: the first lands at the lowest version and commit sequence.
    /// </summary>
    public async Task CommitAsync(NpgsqlConnection conn, Guid streamId, IReadOnlyList<(Guid EventId, decimal Amount)> events, CancellationToken ct) {
      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      var messages = new List<string>();
      foreach (var (eventId, amount) in events) {
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
      await using var stamp = conn.CreateCommand();
      stamp.CommandText = "SELECT stamp_pending_commit_sequences(10000, false)";
      _ = await stamp.ExecuteScalarAsync(ct);
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
