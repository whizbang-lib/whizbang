using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End-to-end integration test: real Postgres emits <c>pg_notify('wh_work', 'outbox')</c>,
/// the <see cref="PgWorkNotificationListener"/> receives it and fires <c>OnSignal</c>,
/// <see cref="ClaimWorker"/>'s subscriber calls <c>RequestImmediatePoll</c>, and the next
/// <c>claim_work</c> tick lands measurably faster than the configured polling interval.
/// </summary>
/// <remarks>
/// Locks the wake-path linkage at the cross-layer boundary. Polling-only tests assert the
/// floor; this test asserts that NOTIFY actually accelerates burst latency.
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
[Category("Shard3")]
public class ClaimWorkerNotificationWakeIntegrationTests : EFCoreTestBase {

  /// <summary>Captures every ClaimWorkAsync call's timestamp so we can compare wake-fired vs polling-fired.</summary>
  private sealed class TimestampingCoordinator : IWorkCoordinator {
    public List<DateTimeOffset> ClaimCallTimes { get; } = [];
    public TaskCompletionSource<int> SecondCallSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      lock (ClaimCallTimes) {
        ClaimCallTimes.Add(DateTimeOffset.UtcNow);
        if (ClaimCallTimes.Count >= 2) {
          SecondCallSeen.TrySetResult(ClaimCallTimes.Count);
        }
      }
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken ct = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken ct = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default) => Task.FromResult(true);
  }

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  [Test]
  public async Task ClaimWorker_OnPgNotify_WakesAndPollsBeforeNextPollIntervalAsync() {
    // Setup: long polling interval (5 seconds) so any tick that lands sooner than that
    // had to come from the wake path. Listener wired directly into ClaimWorker so
    // OnSignal calls RequestImmediatePoll.
    var coord = new TimestampingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var listenerConfig = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var listenerInstanceProvider = new Whizbang.Core.Observability.ServiceInstanceProvider(listenerConfig);
    var listenerOptions = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify
    };
    var sharedConn = new PgSharedNotifyConnection(
      Options.Create(listenerOptions),
      listenerConfig,
      listenerInstanceProvider,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);
    var listener = new PgWorkNotificationListener(
      sharedConn, sharedConn, listenerInstanceProvider,
      NullLogger<PgWorkNotificationListener>.Instance);

    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      listener,
      gate,
      Options.Create(new ClaimWorkerOptions {
        // Parked far beyond the wait window below: polling CANNOT explain a second claim
        // inside 15 s, so the wake path is the only possible cause. That makes the proof
        // structural instead of chronometric — CI scheduling jitter can slow a working wake
        // past a tight wall-clock bound, and a red build for a healthy path teaches nothing.
        PollingIntervalMilliseconds = 60_000,
        PollingMaxIntervalMilliseconds = 60_000
      }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await ((IHostedService)sharedConn).StartAsync(cts.Token);
    // Wait for the shared conn's startup probe to succeed before subscribing the listener.
    var probeDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!sharedConn.IsAvailable && DateTimeOffset.UtcNow < probeDeadline) { await Task.Delay(50); }
    await ((IHostedService)listener).StartAsync(cts.Token);
    await worker.StartAsync(cts.Token);

    // Wait until both the listener is healthy AND the first claim cycle has fired.
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!listener.IsHealthy && DateTimeOffset.UtcNow < deadline) { await Task.Delay(50); }
    await Assert.That(listener.IsHealthy).IsTrue();
    while (coord.ClaimCallTimes.Count == 0 && DateTimeOffset.UtcNow < deadline) { await Task.Delay(50); }
    await Assert.That(coord.ClaimCallTimes.Count).IsGreaterThanOrEqualTo(1)
      .Because("worker should have completed at least the first claim before we issue the wake");
    var firstCallAt = coord.ClaimCallTimes[0];

    // Fire the wake. The next claim should land WELL before firstCallAt + 5s polling interval.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var notifyAt = DateTimeOffset.UtcNow;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = $"SELECT pg_notify('wh_work_i_{listenerInstanceProvider.InstanceId}', 'outbox')";
      _ = await cmd.ExecuteScalarAsync();
    }

    await coord.SecondCallSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));
    var secondCallAt = coord.ClaimCallTimes[1];
    var wakeLatency = secondCallAt - notifyAt;

    // Sub-second on a healthy listener, but the ASSERTION is deliberately structural, not
    // chronometric: polling is parked at 60 s, so a claim arriving inside the 15 s wait
    // above can only have come from the wake. Bounding on wall-clock instead would fail
    // whenever CI starves a thread — turning a working wake path into a red build.
    await Assert.That(wakeLatency).IsLessThan(TimeSpan.FromSeconds(15))
      .Because("the claim arrived inside the wait window while polling was parked at 60s — "
             + "NOTIFY → OnSignal → RequestImmediatePoll → claim_work is the only path that explains it");
    await Assert.That(secondCallAt - firstCallAt).IsLessThan(TimeSpan.FromSeconds(60))
      .Because("the wake-fired tick must land before the parked polling interval would have");

    await worker.StopAsync(CancellationToken.None);
    await ((IHostedService)listener).StopAsync(CancellationToken.None);
    await ((IHostedService)sharedConn).StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ClaimWorker_StoreIntoDrainedHotStream_EdgeDoorbellWakesOwnerAsync() {
    // The end-to-end "quick responses" lock for migration 114's empty→non-empty edge:
    // a REAL store_inbox_messages call into a HOT (pinned) but DRAINED stream must wake
    // the owning worker's claim loop well before the polling interval would. This is the
    // exact interactive-hop scenario: the consumer caught up, the stream idled, the next
    // message arrives — under the cold-only gate nothing rang and the hop waited on the
    // 5 s notify-healthy poll.
    //
    // Sequencing: the first store (which pins the stream to this instance and rings the
    // brand-new-stream doorbell) and the drain-mark both happen BEFORE the listener
    // starts, so that doorbell is dropped by design (pg_notify to a channel nobody
    // LISTENs on) and the only signal that can produce the measured second claim is the
    // second store's edge doorbell.
    var coord = new TimestampingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var listenerConfig = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var listenerInstanceProvider = new Whizbang.Core.Observability.ServiceInstanceProvider(listenerConfig);
    var ownerInstanceId = listenerInstanceProvider.InstanceId;

    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }

    await using (var reg = conn.CreateCommand()) {
      reg.CommandText = @"
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
        ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
      reg.Parameters.AddWithValue("id", ownerInstanceId);
      await reg.ExecuteNonQueryAsync();
    }

    var streamId = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    var firstMsgId = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    await _storeInboxMessageAsync(conn, ownerInstanceId, firstMsgId, streamId);
    await using (var drain = conn.CreateCommand()) {
      drain.CommandText = "UPDATE wh_inbox SET processed_at = NOW() WHERE message_id = @mid";
      drain.Parameters.AddWithValue("mid", firstMsgId);
      _ = await drain.ExecuteNonQueryAsync();
    }

    var listenerOptions = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify
    };
    var sharedConn = new PgSharedNotifyConnection(
      Options.Create(listenerOptions),
      listenerConfig,
      listenerInstanceProvider,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);
    var listener = new PgWorkNotificationListener(
      sharedConn, sharedConn, listenerInstanceProvider,
      NullLogger<PgWorkNotificationListener>.Instance);

    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      listener,
      gate,
      Options.Create(new ClaimWorkerOptions {
        // Parked far beyond the wait window below: polling CANNOT explain a second claim
        // inside 15 s, so the wake path is the only possible cause. That makes the proof
        // structural instead of chronometric — CI scheduling jitter can slow a working wake
        // past a tight wall-clock bound, and a red build for a healthy path teaches nothing.
        PollingIntervalMilliseconds = 60_000,
        PollingMaxIntervalMilliseconds = 60_000
      }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await ((IHostedService)sharedConn).StartAsync(cts.Token);
    var probeDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!sharedConn.IsAvailable && DateTimeOffset.UtcNow < probeDeadline) { await Task.Delay(50); }
    await ((IHostedService)listener).StartAsync(cts.Token);
    await worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!listener.IsHealthy && DateTimeOffset.UtcNow < deadline) { await Task.Delay(50); }
    await Assert.That(listener.IsHealthy).IsTrue();
    while (coord.ClaimCallTimes.Count == 0 && DateTimeOffset.UtcNow < deadline) { await Task.Delay(50); }
    await Assert.That(coord.ClaimCallTimes.Count).IsGreaterThanOrEqualTo(1)
      .Because("worker should have completed at least the first claim before the measured store");

    // The measured hop: a real store into the drained hot stream. Its edge doorbell —
    // NOT the 5 s poll — must produce the next claim.
    var notifyAt = DateTimeOffset.UtcNow;
    await _storeInboxMessageAsync(conn, ownerInstanceId, (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo(), streamId);

    await coord.SecondCallSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));
    var secondCallAt = coord.ClaimCallTimes[1];
    var wakeLatency = secondCallAt - notifyAt;

    // Structural, not chronometric: polling is parked at 60 s, so a claim inside the 15 s
    // wait can only be the edge doorbell. A wall-clock bound here would redden the build
    // whenever CI starves a thread, without the doorbell having failed at all.
    await Assert.That(wakeLatency).IsLessThan(TimeSpan.FromSeconds(15))
      .Because("store into a drained hot stream → edge NOTIFY (migration 114) → OnSignal → RequestImmediatePoll → claim; "
             + "under the cold-only gate nothing rang for hot streams and this hop waited out the poll, which is parked at 60 s here.");

    await worker.StopAsync(CancellationToken.None);
    await ((IHostedService)listener).StopAsync(CancellationToken.None);
    await ((IHostedService)sharedConn).StopAsync(CancellationToken.None);
  }

  private static async Task _storeInboxMessageAsync(NpgsqlConnection conn, Guid instanceId, Guid messageId, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM store_inbox_messages(@p_msgs::jsonb, @p_inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    cmd.Parameters.Add(new NpgsqlParameter("p_msgs", NpgsqlTypes.NpgsqlDbType.Jsonb) {
      Value = $$"""
        [{
          "MessageId": "{{messageId}}",
          "HandlerName": "TestHandler",
          "EnvelopeType": "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
          "MessageType": "Test.X, Test",
          "Envelope": {"p":1},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": true
        }]
        """
    });
    cmd.Parameters.AddWithValue("p_inst", instanceId);
    _ = await cmd.ExecuteScalarAsync();
  }
}
