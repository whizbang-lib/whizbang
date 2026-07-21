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
    public Task RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default) => Task.CompletedTask;
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
        PollingIntervalMilliseconds = 5_000,
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

    // The wake-path latency should be sub-second on a healthy listener; we assert ≤ 2s
    // for safety margin in CI, but well under the 5s polling interval that would be the
    // floor without wake.
    await Assert.That(wakeLatency).IsLessThan(TimeSpan.FromSeconds(2))
      .Because("NOTIFY → OnSignal → RequestImmediatePoll → claim_work should fire within ~50ms; allow 2s for CI jitter");
    await Assert.That(secondCallAt - firstCallAt).IsLessThan(TimeSpan.FromSeconds(5))
      .Because("the wake-fired tick must land before the 5s polling interval would have");

    await worker.StopAsync(CancellationToken.None);
    await ((IHostedService)listener).StopAsync(CancellationToken.None);
    await ((IHostedService)sharedConn).StopAsync(CancellationToken.None);
  }
}
