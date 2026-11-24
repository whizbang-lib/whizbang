// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper coordinator surfaces the ownership notices as the EF Core one does (#2554, #2556): a claim in map mode
/// carries each stream's partition epoch; a claim that found no valid partition map is counted and warned once per
/// streak; a fenced write is counted by kind; a fenced handler commit is reported fenced, not failed.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
public class DapperOwnershipNoticesTests : PostgresTestBase {
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private const int PARTITION = 3;
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");

  [Test]
  public async Task AClaimInMapMode_CarriesTheEpochOfEachStreamsPartitionAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    await _perspectiveEventAsync(conn, stream);

    var batch = await _build().ClaimWorkAsync(new ClaimWorkRequest(_a, "test", "test-host", 1, MaxStreams: 10));

    await Assert.That(batch.StreamEpochs[stream]).IsEqualTo(1L);
    await Assert.That(batch.StreamPartitions[stream])
      .IsEqualTo(await conn.ExecuteScalarAsync<int>("SELECT compute_partition(@stream, 10000)", new { stream }));
  }

  [Test]
  public async Task AClaimWithNoValidMap_IsCounted_AndWarnedOncePerStreakAsync() {
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await conn.ExecuteAsync("SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds')",
      new { role = PartitionAssignerOptions.ROLE, a = _a, epoch, members = new[] { _a, _b } });
    await _perspectiveEventAsync(conn, (Guid)TrackedGuid.New());
    var metrics = _metrics();
    using var observer = new CounterObserver(metrics, "whizbang.work_coordinator.partition_map_unavailable");
    var logger = new CaptureLogger();
    var coordinator = _build(logger, metrics);
    var request = new ClaimWorkRequest(_a, "test", "test-host", 1, MaxStreams: 10);
    await conn.ExecuteAsync("""
      UPDATE wh_published_partition_assignment SET lease_expires_at = NOW() - INTERVAL '1 second';
      UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '1 hour' WHERE instance_id = @b;
      """, new { b = _b });

    var first = await coordinator.ClaimWorkAsync(request);
    _ = await coordinator.ClaimWorkAsync(request);

    await Assert.That(first.PartitionMapUnavailable).IsTrue();
    await Assert.That(observer.Total()).IsEqualTo(2L).Because("every claim that waited for a map is counted");
    await Assert.That(logger.Warnings).IsEqualTo(1).Because("the warning is written once per streak");
  }

  [Test]
  public async Task AFencedWrite_IsCountedByKind_AndAFencedHandlerCommitIsNotAFailureAsync() {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var message = (Guid)TrackedGuid.New();
    await conn.ExecuteAsync("""
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number,
         instance_id, lease_expiry)
      VALUES (@id, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, clock_timestamp(), @stream, @p, @a, NOW() + INTERVAL '5 minutes')
      """, new { id = message, stream = (Guid)TrackedGuid.New(), p = PARTITION, a = _a });
    var inbox = (Guid)TrackedGuid.New();
    await conn.ExecuteAsync("""
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        VALUES (@id, 'TestHandler', 'TestCommand', '{}', '{}', NOW() - INTERVAL '1 minute', @stream, FALSE, 150)
        RETURNING message_id, stream_id, received_at
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT m.message_id, m.stream_id, m.received_at, 150, FALSE, 0, 0, @p, @a, NOW() + INTERVAL '5 minutes' FROM m
      """, new { id = inbox, stream = (Guid)TrackedGuid.New(), p = PARTITION, a = _a });
    await conn.ExecuteAsync("""
      UPDATE wh_partition_ownership SET owner_instance_id = @b, epoch = epoch + 1 WHERE partition_number = @p
      """, new { b = _b, p = PARTITION });
    var metrics = _metrics();
    using var observer = new CounterObserver(metrics, "whizbang.work_coordinator.fenced_writes");
    var coordinator = _build(metrics: metrics);
    var request = new HandlerCommitRequest((Guid)TrackedGuid.New(), _a, "test", "test-host", 1, 10000,
      new HandlerInboxCompletion(inbox, 4)) { PartitionEpoch = 1 };

    var affected = await coordinator.CompleteOutboxPublishedAsync([message], [1L], debugMode: false);
    var results = await coordinator.CommitHandlerBatchAsync([request]);
    await coordinator.CommitHandlerResultAsync(request);
    await coordinator.CompletePerspectiveAsync([], [], [1L], debugMode: false);
    await coordinator.FlushCompletionsAsync(new FlushCompletionsRequest(OutboxIds: [message], OutboxEpochs: [1L]));

    await Assert.That(affected).IsEqualTo(0);
    await Assert.That(results[0].Fenced).IsTrue();
    await Assert.That(observer.Total()).IsEqualTo(4L)
      .Because("the publish mark, the batch item, the single commit and the flush were each fenced and counted");
  }

  // --- helpers --------------------------------------------------------------------------------------

  private DapperWorkCoordinator _build(ILogger<DapperWorkCoordinator>? logger = null, WorkCoordinatorMetrics? metrics = null) =>
    new(ConnectionString, new JsonSerializerOptions { TypeInfoResolver = InfrastructureJsonContext.Default }, logger, metrics: metrics);

  private static WorkCoordinatorMetrics _metrics() =>
    new(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<long> _mapModeAsync(NpgsqlConnection conn) {
    foreach (var instance in new[] { _a, _b }) {
      await conn.ExecuteAsync("SELECT record_heartbeat(@inst, 'test', 'test-host', 1, @metadata::jsonb, NULL, NULL, NULL, 'pooled')",
        new { inst = instance, metadata = SUPPORTS_MAP });
    }
    var epoch = await conn.ExecuteScalarAsync<long>(
      "SELECT epoch FROM wh_vote_role(@role, @a, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'",
      new { role = PartitionAssignerOptions.ROLE, a = _a });
    await conn.ExecuteAsync("SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds')",
      new { role = PartitionAssignerOptions.ROLE, a = _a, epoch, members = new[] { _a } });
    var answer = await conn.ExecuteScalarAsync<string>("SELECT wh_open_partition_ownership_by_map(@role, @a, @epoch)",
      new { role = PartitionAssignerOptions.ROLE, a = _a, epoch });
    if (answer != "opened") {
      throw new InvalidOperationException($"precondition: the switch did not turn on ({answer})");
    }
    return epoch;
  }

  private static Task<int> _perspectiveEventAsync(NpgsqlConnection conn, Guid stream) =>
    conn.ExecuteAsync("""
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
      VALUES (gen_random_uuid(), @stream, 'Notices.Perspective', gen_random_uuid(), compute_partition(@stream, 10000), 0, 0,
              clock_timestamp() - INTERVAL '1 minute')
      """, new { stream });

  private sealed class CaptureLogger : ILogger<DapperWorkCoordinator> {
    private int _warnings;
    public int Warnings => Volatile.Read(ref _warnings);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning && formatter(state, exception).Contains("partition map", StringComparison.Ordinal)) {
        Interlocked.Increment(ref _warnings);
      }
    }
  }

  private sealed class CounterObserver : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly List<long> _cells = [];

    public CounterObserver(WorkCoordinatorMetrics metrics, string name) {
      _listener.InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument.Meter, metrics.CommitHandlerFallbacks.Meter) && instrument.Name == name) {
          l.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((_, value, _, _) => {
        lock (_cells) { _cells.Add(value); }
      });
      _listener.Start();
    }

    public long Total() {
      lock (_cells) { _cells.Clear(); }
      _listener.RecordObservableInstruments();
      lock (_cells) { return _cells.Sum(); }
    }

    public void Dispose() => _listener.Dispose();
  }
}
