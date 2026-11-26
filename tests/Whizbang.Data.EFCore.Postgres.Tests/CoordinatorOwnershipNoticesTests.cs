// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The EF Core coordinator surfaces the ownership notices the SQL raises, so neither is silent: a claim made in map mode
/// carries the epoch of each claimed stream's partition (#2556); a claim that found no valid partition map is counted and
/// warned once per streak (#2554); a write fenced by the partition epoch is counted by kind and logged (#2556), and a
/// fenced handler commit is reported fenced, not failed.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorOwnershipNotices.cs</code-under-test>
[Category("Shard5")]
public class CoordinatorOwnershipNoticesTests : EFCoreTestBase {
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private const int PARTITION = 3;
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");

  // --- pure folds -----------------------------------------------------------------------------------

  [Test]
  public async Task AFencedNotice_IsReadAsItsKindAndCount_AndAnyOtherIsNotAsync() {
    var read = CoordinatorOwnershipNotices.TryReadFenced("whizbang.fenced", "outbox_completion=3", out var kind, out var count);
    var other = CoordinatorOwnershipNotices.TryReadFenced("whizbang.other", "outbox_completion=3", out _, out _);
    var noDetail = CoordinatorOwnershipNotices.TryReadFenced("whizbang.fenced", null, out _, out _);
    var noCount = CoordinatorOwnershipNotices.TryReadFenced("whizbang.fenced", "outbox_completion=", out _, out _);
    var noKind = CoordinatorOwnershipNotices.TryReadFenced("whizbang.fenced", "=3", out _, out _);

    await Assert.That(read).IsTrue();
    await Assert.That(kind).IsEqualTo("outbox_completion");
    await Assert.That(count).IsEqualTo(3L);
    await Assert.That(other).IsFalse();
    await Assert.That(noDetail).IsFalse();
    await Assert.That(noCount).IsFalse();
    await Assert.That(noKind).IsFalse();
  }

  [Test]
  public async Task ClaimedTenures_FoldOneEntryPerStream_KeepingTheLowerEpoch_AndSkipRowsWithoutOneAsync() {
    var stream = (Guid)TrackedGuid.New();
    var other = (Guid)TrackedGuid.New();
    (Guid? Stream, int? Partition, long? Epoch)[] rows = [
      (stream, 5, 4L), (stream, 5, 3L), (other, 6, null), (null, 7, 1L)];

    var epochs = ClaimedTenures.Epochs(rows, r => r.Stream, r => r.Epoch);
    var partitions = ClaimedTenures.Partitions(rows, r => r.Stream, r => r.Epoch is null ? null : r.Partition);

    await Assert.That(epochs).Count().IsEqualTo(1);
    await Assert.That(epochs[stream]).IsEqualTo(3L).Because("a write under the lower epoch can only be fenced more, never less");
    await Assert.That(partitions).Count().IsEqualTo(1);
    await Assert.That(partitions[stream]).IsEqualTo(5);
  }

  // --- the claim ------------------------------------------------------------------------------------

  [Test]
  public async Task AClaimInMapMode_CarriesTheEpochOfEachStreamsPartitionAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    await _perspectiveEventAsync(conn, stream);
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    var batch = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(_a, "test", "test-host", 1, MaxStreams: 10));

    await Assert.That(batch.PerspectiveStreamIds).Contains(stream);
    await Assert.That(batch.StreamEpochs[stream]).IsEqualTo(1L).Because("A owns every partition at epoch 1");
    await Assert.That(batch.StreamPartitions[stream]).IsEqualTo(
      await _scalarAsync<int>(conn, "SELECT compute_partition(@s, 10000)", ("s", stream)))
      .Because("a perspective stream's partition is its stream's, as the store computed it");
    await Assert.That(batch.PartitionMapUnavailable).IsFalse();
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task TheClaimsEpochs_AreWhHeldEpochsAnswers_InEveryCaseAsync(bool mapMode) {
    await using var conn = await _openAsync();
    if (mapMode) {
      _ = await _mapModeAsync(conn);
      // Partition 5 handing over (still A's), partition 6 B's.
      await _execAsync(conn, "UPDATE wh_partition_ownership SET state = 'handing_over', next_owner_instance_id = @b, handover_deadline = NOW() + INTERVAL '30 seconds' WHERE partition_number = 5", ("b", _b));
      await _execAsync(conn, "UPDATE wh_partition_ownership SET owner_instance_id = @b, epoch = epoch + 1 WHERE partition_number = 6", ("b", _b));
    }
    var stream = (Guid)TrackedGuid.New();
    // Rows as a claim returns them: a partition A owns, one handing over, one B owns, one past the table, none with a
    // stream (the stream's partition), and none at all.
    var rows = $"(VALUES (4, NULL::uuid), (5, NULL::uuid), (6, NULL::uuid), (10006, NULL::uuid), (NULL::int, '{stream}'::uuid), (NULL::int, NULL::uuid)) AS v(partition_number, work_stream_id)";
    var sql = ClaimedTenures.ProjectEpochs("", "c.partition_number, c.work_stream_id", rows, "@a", "10000");

    var mismatches = await _scalarAsync<long>(conn,
      $"SELECT count(*) FROM ({sql}) p WHERE p.partition_epoch IS DISTINCT FROM wh_held_epoch(COALESCE(p.partition_number, compute_partition(p.work_stream_id, 10000)), @a)",
      ("a", _a));

    await Assert.That(mismatches).IsEqualTo(0L)
      .Because("the claim's set-wise projection is the same rule as wh_held_epoch, case by case");
  }

  [Test]
  public async Task AClaimWithTheSwitchOff_CarriesNoEpochAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _perspectiveEventAsync(conn, stream);
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    var batch = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(_a, "test", "test-host", 1, MaxStreams: 10));

    await Assert.That(batch.StreamEpochs).IsEmpty().Because("off, nothing is fenced, so nothing carries an epoch");
  }

  [Test]
  public async Task AClaimWithNoValidMap_IsCounted_AndWarnedOncePerStreakAsync() {
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _publishAsync(conn, epoch, [_a, _b]);
    // The claim judges the map only when there is work to claim.
    _ = await _perspectiveEventAsync(conn, (Guid)TrackedGuid.New());
    var metrics = _metrics();
    using var observer = new CounterObserver(metrics, "whizbang.work_coordinator.partition_map_unavailable");
    var logger = new CaptureLogger();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions(), logger, metrics);
    var request = new ClaimWorkRequest(_a, "test", "test-host", 1, MaxStreams: 10);

    await _invalidateMapAsync(conn);
    var first = await coordinator.ClaimWorkAsync(request);
    _ = await coordinator.ClaimWorkAsync(request);
    var warnedInTheFirstStreak = logger.Warnings();
    await _execAsync(conn, "UPDATE wh_published_partition_assignment SET lease_expires_at = NOW() + INTERVAL '90 seconds'");
    var valid = await coordinator.ClaimWorkAsync(request);
    await _invalidateMapAsync(conn);
    _ = await coordinator.ClaimWorkAsync(request);

    await Assert.That(first.PartitionMapUnavailable).IsTrue();
    await Assert.That(valid.PartitionMapUnavailable).IsFalse();
    await Assert.That(observer.Total()).IsEqualTo(3L).Because("every claim that waited for a map is counted");
    await Assert.That(warnedInTheFirstStreak).IsEqualTo(1).Because("the warning is written once per streak, not per claim");
    await Assert.That(logger.Warnings()).IsEqualTo(2).Because("a claim that found a map ends the streak, so the next wait warns again");
  }

  // --- fenced writes --------------------------------------------------------------------------------

  [Test]
  public async Task AFencedWrite_IsCountedByKind_AndLoggedAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var message = await _outboxRowAsync(conn);
    await _moveOnAsync(conn);
    var metrics = _metrics();
    using var observer = new CounterObserver(metrics, "whizbang.work_coordinator.fenced_writes");
    var logger = new CaptureLogger();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions(), logger, metrics);

    var affected = await coordinator.CompleteOutboxPublishedAsync([message], [1L], debugMode: false);

    await Assert.That(affected).IsEqualTo(0);
    await Assert.That(observer.ByKind()).IsEquivalentTo([("outbox_completion", 1L)]);
    await Assert.That(logger.Debugs(m => m.Contains("Fenced 1 outbox_completion", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("a fenced write is never silent: the log names what was fenced and where");
  }

  [Test]
  public async Task AFencedPerspectiveCompletion_IsCountedAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var work = await _perspectiveEventAsync(conn, (Guid)TrackedGuid.New(), holder: _a);
    await _moveOnAsync(conn);
    var metrics = _metrics();
    using var observer = new CounterObserver(metrics, "whizbang.work_coordinator.fenced_writes");
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions(), null, metrics);

    await coordinator.CompletePerspectiveAsync([], [work], [1L], debugMode: false);

    await Assert.That(observer.ByKind()).IsEquivalentTo([("perspective_event_completion", 1L)]);
  }

  [Test]
  public async Task AFencedFlush_IsCountedAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var message = await _outboxRowAsync(conn);
    await _moveOnAsync(conn);
    var metrics = _metrics();
    using var observer = new CounterObserver(metrics, "whizbang.work_coordinator.fenced_writes");
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions(), null, metrics);

    await coordinator.FlushCompletionsAsync(new FlushCompletionsRequest(OutboxIds: [message], OutboxEpochs: [1L]));

    await Assert.That(observer.ByKind()).IsEquivalentTo([("outbox_completion", 1L)]);
  }

  [Test]
  public async Task AHandlerCommitUnderAStaleEpoch_IsReportedFenced_NotFailedAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var inbox = await _inboxRowAsync(conn);
    await _moveOnAsync(conn);
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    var handler = (Guid)TrackedGuid.New();
    var request = new HandlerCommitRequest(handler, _a, "test", "test-host", 1, 10000, new HandlerInboxCompletion(inbox, 4)) {
      PartitionEpoch = 1
    };

    var results = await coordinator.CommitHandlerBatchAsync([request]);
    await coordinator.CommitHandlerResultAsync(request);

    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Fenced).IsTrue();
    await Assert.That(results[0].Success).IsFalse();
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT processed_at IS NULL FROM wh_inbox_state WHERE message_id = @m", ("m", inbox)))
      .IsTrue().Because("neither the batch nor the single commit lands under a stale epoch");
  }

  // --- helpers --------------------------------------------------------------------------------------

  private static WorkCoordinatorMetrics _metrics() =>
    new(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  private sealed class CaptureLogger : ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>> {
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_entries) { _entries.Add((logLevel, formatter(state, exception))); }
    }
    public int Warnings() {
      lock (_entries) { return _entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("partition map", StringComparison.Ordinal)); }
    }
    public int Debugs(Func<string, bool> match) {
      lock (_entries) { return _entries.Count(e => e.Level == LogLevel.Debug && match(e.Message)); }
    }
  }

  /// <summary>Reads one passive counter's running totals per kind tag.</summary>
  private sealed class CounterObserver : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly List<(long Value, string? Kind)> _cells = [];

    public CounterObserver(WorkCoordinatorMetrics metrics, string name) {
      _listener.InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument.Meter, metrics.CommitHandlerFallbacks.Meter) && instrument.Name == name) {
          l.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
        string? kind = null;
        foreach (var tag in tags) {
          if (tag.Key == WorkCoordinatorMetrics.KIND_TAG) {
            kind = tag.Value as string;
          }
        }
        lock (_cells) { _cells.Add((value, kind)); }
      });
      _listener.Start();
    }

    private List<(long Value, string? Kind)> _snapshot() {
      lock (_cells) { _cells.Clear(); }
      _listener.RecordObservableInstruments();
      lock (_cells) { return [.. _cells]; }
    }

    public long Total() => _snapshot().Sum(c => c.Value);

    public List<(string, long)> ByKind() => [.. _snapshot().Where(c => c.Value > 0).Select(c => (c.Kind ?? "", c.Value))];

    public void Dispose() => _listener.Dispose();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<long> _mapModeAsync(NpgsqlConnection conn) {
    await _execAsync(conn, "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, @metadata::jsonb, NULL, NULL, NULL, 'pooled')",
      ("inst", _a), ("metadata", SUPPORTS_MAP));
    await _execAsync(conn, "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, @metadata::jsonb, NULL, NULL, NULL, 'pooled')",
      ("inst", _b), ("metadata", SUPPORTS_MAP));
    var epoch = Convert.ToInt64(await _scalarAsync<object>(conn,
      "SELECT epoch FROM wh_vote_role(@role, @a, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a)), System.Globalization.CultureInfo.InvariantCulture);
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds')";
      cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
      cmd.Parameters.AddWithValue("a", _a);
      cmd.Parameters.AddWithValue(nameof(epoch), epoch);
      cmd.Parameters.Add(new NpgsqlParameter("members", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { _a } });
      await cmd.ExecuteNonQueryAsync();
    }
    var answer = await _scalarAsync<string>(conn, "SELECT wh_open_partition_ownership_by_map(@role, @a, @epoch)",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a), ("epoch", epoch));
    if (answer != "opened") {
      throw new InvalidOperationException($"precondition: the switch did not turn on ({answer})");
    }
    return epoch;
  }

  /// <summary>
  /// The published map's lease has run out and one of its members (B) is not live, so no claim may use it. The claimer
  /// (A) stays live: a claimer that may not claim at all returns before the map is judged.
  /// </summary>
  private static Task _invalidateMapAsync(NpgsqlConnection conn) =>
    _execAsync(conn, """
      UPDATE wh_published_partition_assignment SET lease_expires_at = NOW() - INTERVAL '1 second';
      UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '1 hour' WHERE instance_id = @b;
      """, ("b", _b));

  private static async Task _publishAsync(NpgsqlConnection conn, long epoch, Guid[] members) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds')";
    cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    cmd.Parameters.AddWithValue("a", _a);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(members), NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = members });
    await cmd.ExecuteNonQueryAsync();
  }

  private static Task _moveOnAsync(NpgsqlConnection conn) =>
    _execAsync(conn, """
      UPDATE wh_partition_ownership
      SET owner_instance_id = @b, epoch = epoch + 1, state = 'active', next_owner_instance_id = NULL, handover_deadline = NULL
      WHERE partition_number = @p
      """, ("b", _b), ("p", PARTITION));

  private static async Task<Guid> _outboxRowAsync(NpgsqlConnection conn) {
    var id = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number,
         instance_id, lease_expiry)
      VALUES (@id, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, clock_timestamp(), @stream, @p, @a, NOW() + INTERVAL '5 minutes')
      """, ("id", id), ("stream", (Guid)TrackedGuid.New()), ("p", PARTITION), ("a", _a));
    return id;
  }

  private static async Task<Guid> _perspectiveEventAsync(NpgsqlConnection conn, Guid stream, Guid? holder = null) {
    var work = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, instance_id, lease_expiry)
      VALUES (@w, @stream, 'Notices.Perspective', @event, CASE WHEN @holder::uuid IS NULL THEN compute_partition(@stream, 10000) ELSE @p END,
              0, 0, clock_timestamp() - INTERVAL '1 minute', @holder::uuid,
              CASE WHEN @holder::uuid IS NULL THEN NULL ELSE NOW() + INTERVAL '5 minutes' END)
      """, ("w", work), ("stream", stream), ("event", (Guid)TrackedGuid.New()), ("p", PARTITION),
      ("holder", (object?)holder ?? DBNull.Value));
    return work;
  }

  private static async Task<Guid> _inboxRowAsync(NpgsqlConnection conn) {
    var id = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        VALUES (@id, 'TestHandler', 'TestCommand', '{}', '{}', NOW() - INTERVAL '1 minute', @stream, FALSE, 150)
        RETURNING message_id, stream_id, received_at
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT m.message_id, m.stream_id, m.received_at, 150, FALSE, 0, 0, @p, @a, NOW() + INTERVAL '5 minutes'
      FROM m
      """, ("id", id), ("stream", (Guid)TrackedGuid.New()), ("p", PARTITION), ("a", _a));
    return id;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }
}
