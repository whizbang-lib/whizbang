// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
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

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Every write the workers make names the partition epoch its work was claimed under, with no worker carrying it
/// (#2556): the coordinator records the tenures each claim returns and the work ids each fetch returns, and fills the
/// epochs into every completion, failure and handler commit it writes. So the work a worker claimed, fetched and
/// finished after its partition moved on lands nowhere, though the worker passed no epoch at all.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard5")]
public class CoordinatorNamesTheClaimedEpochTests : EFCoreTestBase {
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");

  [Test]
  public async Task AnOutboxCompletion_OfWorkFetchedBeforeThePartitionMoved_IsFencedAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var message = await _outboxRowAsync(conn, stream);
    var (coordinator, observer) = _coordinator();

    _ = await coordinator.ClaimWorkAsync(_claim());
    var fetched = await coordinator.FetchOutboxBatchAsync([stream], _a, 10, null);
    await _moveOnAsync(conn, stream);
    var affected = await coordinator.CompleteOutboxPublishedAsync([message], debugMode: false);

    await Assert.That(fetched.Select(r => r.MessageId)).Contains(message);
    await Assert.That(affected).IsEqualTo(0).Because("the coordinator named the epoch the work was claimed under");
    await Assert.That(observer.Total()).IsEqualTo(1L);
    await Assert.That(await _countAsync(conn, "wh_outbox", "message_id", message)).IsEqualTo(1L);
  }

  [Test]
  public async Task AnOutboxCompletion_WhileThePartitionIsStillOwned_LandsAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var message = await _outboxRowAsync(conn, stream);
    var (coordinator, observer) = _coordinator();

    _ = await coordinator.ClaimWorkAsync(_claim());
    _ = await coordinator.FetchOutboxBatchAsync([stream], _a, 10, null);
    var affected = await coordinator.CompleteOutboxPublishedAsync([message], debugMode: false);

    await Assert.That(affected).IsEqualTo(1);
    await Assert.That(observer.Total()).IsEqualTo(0L);
  }

  [Test]
  public async Task APerspectiveCompletion_OfWorkFetchedBeforeThePartitionMoved_IsFencedAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var work = await _perspectiveEventAsync(conn, stream);
    var (coordinator, observer) = _coordinator();

    _ = await coordinator.ClaimWorkAsync(_claim());
    var fetched = await coordinator.GetStreamEventsAsync(_a, [stream]);
    await _moveOnAsync(conn, stream);
    await coordinator.CompletePerspectiveAsync([], [work], debugMode: false);

    await Assert.That(fetched.Select(e => e.EventWorkId)).Contains(work);
    await Assert.That(await _countAsync(conn, "wh_perspective_events", "event_work_id", work)).IsEqualTo(1L);
    await Assert.That(observer.Total()).IsEqualTo(1L);
  }

  [Test]
  public async Task ACursor_ForAStreamClaimedBeforeThePartitionMoved_IsFencedAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    _ = await _perspectiveEventAsync(conn, stream);
    var lastEvent = await _scalarAsync<Guid>(conn, "SELECT event_id FROM wh_perspective_events WHERE stream_id = @s", ("s", stream));
    var (coordinator, observer) = _coordinator();

    _ = await coordinator.ClaimWorkAsync(_claim());
    await _moveOnAsync(conn, stream);
    await coordinator.CompletePerspectiveAsync(
      [new PerspectiveCursorCompletion { StreamId = stream, PerspectiveName = "Named.Perspective", LastEventId = lastEvent, Status = PerspectiveProcessingStatus.Completed }],
      [], debugMode: false);

    await Assert.That(observer.Total()).IsEqualTo(1L).Because("a cursor names its stream's claimed tenure");
  }

  [Test]
  public async Task AHandlerCommit_OfAMessageFetchedBeforeThePartitionMoved_CommitsNothingAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var inbox = await _inboxRowAsync(conn, stream);
    var (coordinator, observer) = _coordinator();

    _ = await coordinator.ClaimWorkAsync(_claim());
    var fetched = await coordinator.FetchInboxBatchAsync([stream], _a, 10, null);
    await _moveOnAsync(conn, stream);
    var results = await coordinator.CommitHandlerBatchAsync(
      [new HandlerCommitRequest((Guid)TrackedGuid.New(), _a, "test", "test-host", 1, 10000, new HandlerInboxCompletion(inbox, 4))]);

    await Assert.That(fetched.Select(r => r.MessageId)).Contains(inbox);
    await Assert.That(results[0].Fenced).IsTrue();
    await Assert.That(observer.Total()).IsEqualTo(1L);
  }

  [Test]
  public async Task AFailure_OfWorkFetchedBeforeThePartitionMoved_IsNotRecordedAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var message = await _outboxRowAsync(conn, stream);
    var (coordinator, observer) = _coordinator();

    _ = await coordinator.ClaimWorkAsync(_claim());
    _ = await coordinator.FetchOutboxBatchAsync([stream], _a, 10, null);
    await _moveOnAsync(conn, stream);
    await coordinator.ReportFailuresAsync(WorkCategory.Outbox,
      [new MessageFailure { MessageId = message, CompletedStatus = MessageProcessingStatus.Stored, Error = "stale owner" }]);

    await Assert.That(await _scalarAsync<object>(conn, "SELECT error FROM wh_outbox WHERE message_id = @m", ("m", message)))
      .IsEqualTo(DBNull.Value);
    await Assert.That(observer.Total()).IsEqualTo(1L);
  }

  [Test]
  public async Task TheCoordinator_ListsWhatThisInstanceHandsOver_AndAcknowledgesItAsync() {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    await _publishAsync(conn, [_a, _b]);
    var (coordinator, _) = _coordinator();

    var handingOver = await coordinator.GetPartitionsHandingOverAsync(_a);
    var none = await coordinator.GetPartitionsHandingOverAsync(_b);
    var flipped = await coordinator.AcknowledgeHandoverAsync(_a, [.. handingOver.Take(2)]);

    await Assert.That(handingOver).Count().IsEqualTo(5_000);
    await Assert.That(handingOver.All(h => h.NextOwner == _b)).IsTrue();
    await Assert.That(none).IsEmpty();
    await Assert.That(flipped).IsEquivalentTo([.. handingOver.Take(2).Select(h => h.Partition)]);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_partition_ownership WHERE state = 'handing_over'"))
      .IsEqualTo(4_998L);
  }

  [Test]
  public async Task TheLeaveStatus_CountsWhatThisInstanceOwns_AndWhoElseCouldTakeItAsync() {
    await using var conn = await _openAsync();
    var (coordinator, _) = _coordinator();
    var off = await coordinator.GetLeaveStatusAsync(_a);
    await _mapModeAsync(conn);

    var on = await coordinator.GetLeaveStatusAsync(_a);

    await Assert.That(off.Owned).IsEqualTo(0).Because("off map mode nothing is owned by the table, so a leave waits for nothing");
    await Assert.That(on).IsEqualTo(new PartitionLeaveStatus(10_000, 1)).Because("A owns every partition, and B could take them");
  }

  [Test]
  public async Task AcknowledgingNothing_FlipsNothing_WithoutARoundTripAsync() {
    var (coordinator, _) = _coordinator();

    var flipped = await coordinator.AcknowledgeHandoverAsync(_a, []);

    await Assert.That(flipped).IsEmpty();
  }

  // --- helpers --------------------------------------------------------------------------------------

  private (EFCoreWorkCoordinator<WorkCoordinationDbContext> Coordinator, CounterObserver Observer) _coordinator() {
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, JsonContextRegistry.CreateCombinedOptions(), metrics: metrics, tenures: new PartitionTenures());
    return (coordinator, new CounterObserver(metrics));
  }

  private static ClaimWorkRequest _claim() => new(_a, "test", "test-host", 1, MaxStreams: 25);

  private sealed class CounterObserver : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly List<long> _cells = [];

    public CounterObserver(WorkCoordinatorMetrics metrics) {
      _listener.InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument.Meter, metrics.CommitHandlerFallbacks.Meter)
            && instrument.Name == "whizbang.work_coordinator.fenced_writes") {
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

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _mapModeAsync(NpgsqlConnection conn) {
    foreach (var instance in new[] { _a, _b }) {
      await _execAsync(conn, "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, @metadata::jsonb, NULL, NULL, NULL, 'pooled')",
        ("inst", instance), ("metadata", SUPPORTS_MAP));
    }
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
  }

  private static async Task _publishAsync(NpgsqlConnection conn, Guid[] members) {
    var epoch = await _scalarAsync<long>(conn, "SELECT epoch FROM wh_published_partition_assignment WHERE singleton");
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds')";
    cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    cmd.Parameters.AddWithValue("a", _a);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(members), NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = members });
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>The flip the handover ends with, for the stream's partition: B owns it at the next epoch.</summary>
  private static Task _moveOnAsync(NpgsqlConnection conn, Guid stream) =>
    _execAsync(conn, """
      UPDATE wh_partition_ownership
      SET owner_instance_id = @b, epoch = epoch + 1, state = 'active', next_owner_instance_id = NULL, handover_deadline = NULL
      WHERE partition_number = compute_partition(@s, 10000)
      """, ("b", _b), ("s", stream));

  private static async Task<Guid> _outboxRowAsync(NpgsqlConnection conn, Guid stream) {
    var id = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
      VALUES (@id, 'test-topic', 'TestEvent', '{}', '{}', 0, 0, clock_timestamp() - INTERVAL '1 minute', @s, compute_partition(@s, 10000))
      """, ("id", id), ("s", stream));
    return id;
  }

  private static async Task<Guid> _perspectiveEventAsync(NpgsqlConnection conn, Guid stream) {
    var work = (Guid)TrackedGuid.New();
    var eventId = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@event, @s, @s, 'TestAggregate', 'TestEvent', 1, NOW() - INTERVAL '1 minute');
      INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES (@event, '{}', '{}')
      """, ("event", eventId), ("s", stream));
    await _execAsync(conn, """
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
      VALUES (@w, @s, 'Named.Perspective', @event, compute_partition(@s, 10000), 0, 0, NOW() - INTERVAL '1 minute')
      """, ("w", work), ("s", stream), ("event", eventId));
    return work;
  }

  private static async Task<Guid> _inboxRowAsync(NpgsqlConnection conn, Guid stream) {
    var id = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        VALUES (@id, 'TestHandler', 'TestCommand', '{}', '{}', NOW() - INTERVAL '1 minute', @s, FALSE, 150)
        RETURNING message_id, stream_id, received_at
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number)
      SELECT m.message_id, m.stream_id, m.received_at, 150, FALSE, 0, 0, compute_partition(@s, 10000) FROM m
      """, ("id", id), ("s", stream));
    return id;
  }

  private static Task<long> _countAsync(NpgsqlConnection conn, string table, string column, Guid id) =>
    _scalarAsync<long>(conn, $"SELECT count(*) FROM {table} WHERE {column} = @id", ("id", id));

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
