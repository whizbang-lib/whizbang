using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The EF Core coordinator's side of #917: it hands the claim the outbox row bound and run, reports a
/// full outbox acquisition on the batch, and continues streams from their leases.
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Shard4")]
public class OutboxStreamRunCoordinatorTests : EFCoreTestBase {

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coord(WorkCoordinationDbContext ctx) =>
    new(ctx, JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  /// <summary>Seeds one stream's rows in arrival order with ascending message ids.</summary>
  private static async Task<List<Guid>> _seedStreamAsync(NpgsqlConnection conn, Guid stream, int rows) {
    var ids = Enumerable.Range(0, rows).Select(_ => Guid.CreateVersion7()).Order().ToArray();
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
      SELECT m.id, 'test-topic', 'TestEvent', '{}', '{}', 0, 0,
             TIMESTAMPTZ '2026-01-01 00:00:00+00' + (m.ord * INTERVAL '1 millisecond'), @stream, 0
      FROM unnest(@ids) WITH ORDINALITY AS m(id, ord)";
    ins.Parameters.AddWithValue("ids", ids);
    ins.Parameters.AddWithValue("stream", stream);
    await ins.ExecuteNonQueryAsync();
    return [.. ids];
  }

  private static ClaimWorkRequest _request(Guid instance, int maxStreams, int? outboxRows, int? run) => new(
    InstanceId: instance,
    ServiceName: "test-svc",
    HostName: "test-host",
    ProcessId: 1,
    MaxStreams: maxStreams,
    MaxOutboxAcquireRows: outboxRows,
    OutboxRunLength: run);

  private static async Task<int> _heldAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_outbox WHERE instance_id = @inst AND processed_at IS NULL";
    cmd.Parameters.AddWithValue("inst", instance);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  [Test]
  public async Task ClaimWorkAsync_PassesTheOutboxBoundAndRun_AndReportsAFullAcquisitionAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coord(ctx);
    var instance = Guid.CreateVersion7();
    var stream = Guid.CreateVersion7();
    _ = await _seedStreamAsync(conn, stream, 30);

    // One stream in the window, a run of 20, a bound of 20: the run fills the bound.
    var batch = await coordinator.ClaimWorkAsync(_request(instance, maxStreams: 1, outboxRows: 20, run: 20));

    await Assert.That(await _heldAsync(conn, instance)).IsEqualTo(20)
      .Because("the coordinator must carry the outbox bound and run to the claim; without them one stream moves a row per claim");
    await Assert.That(batch.OutboxStreamIds).IsEquivalentTo([stream]);
    await Assert.That(batch.OutboxAcquisitionFull).IsTrue()
      .Because("the claim's in-band notice that acquisition filled its bound reaches the batch");
  }

  [Test]
  public async Task ClaimWorkAsync_AcquisitionBelowItsBound_IsNotReportedFullAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coord(ctx);
    var instance = Guid.CreateVersion7();
    _ = await _seedStreamAsync(conn, Guid.CreateVersion7(), 5);

    var batch = await coordinator.ClaimWorkAsync(_request(instance, maxStreams: 1, outboxRows: 20, run: 20));

    await Assert.That(await _heldAsync(conn, instance)).IsEqualTo(5);
    await Assert.That(batch.OutboxAcquisitionFull).IsFalse();
  }

  [Test]
  public async Task ClaimWorkAsync_WithoutAnOutboxBound_KeepsThePreviousClaimAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coord(ctx);
    var instance = Guid.CreateVersion7();
    _ = await _seedStreamAsync(conn, Guid.CreateVersion7(), 5);

    _ = await coordinator.ClaimWorkAsync(_request(instance, maxStreams: 1, outboxRows: null, run: null));

    await Assert.That(await _heldAsync(conn, instance)).IsEqualTo(1)
      .Because("null bound and run pass NULL, and the store's previous bound (the stream window, one row here) applies");
  }

  [Test]
  public async Task ContinueOutboxStreamsAsync_ReturnsTheNextRunAfterTheCursorAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coord(ctx);
    var instance = Guid.CreateVersion7();
    var stream = Guid.CreateVersion7();
    var ids = await _seedStreamAsync(conn, stream, 25);
    _ = await coordinator.ClaimWorkAsync(_request(instance, maxStreams: 1, outboxRows: 100, run: 10));

    var rows = await coordinator.ContinueOutboxStreamsAsync(
      [new OutboxStreamCursor(stream, ids[9])], instance, runLength: 10, maxBytes: null);

    await Assert.That(rows.Select(r => r.MessageId)).IsEquivalentTo(ids.Skip(10).Take(10), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    var first = rows[0];
    await Assert.That(first.StreamId).IsEqualTo(stream);
    await Assert.That(first.Destination).IsEqualTo("test-topic");
    await Assert.That(first.MessageType).IsEqualTo("TestEvent");
    await Assert.That(first.Attempts).IsEqualTo(1)
      .Because("the continuation's lease charges the attempt, as a claim's does");
    await Assert.That(first.IsEvent).IsFalse();
    await Assert.That(first.CommitSequence).IsNull();
    await Assert.That(first.OriginServiceId).IsNull();
    await Assert.That(first.OriginCommitSequence).IsNull();
    await Assert.That(first.Scope).IsNull();
    await Assert.That(first.EnvelopeType).IsNull();
    await Assert.That(first.PartitionNumber).IsEqualTo(0);
    await Assert.That(first.Error).IsNull();
  }

  [Test]
  public async Task ContinueOutboxStreamsAsync_NoStreams_ReturnsNothingWithoutARoundTripAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coord(ctx);

    var rows = await coordinator.ContinueOutboxStreamsAsync([], Guid.CreateVersion7(), runLength: 10, maxBytes: null);

    await Assert.That(rows).IsEmpty();
  }

  [Test]
  public async Task ContinueOutboxStreamsAsync_ReadsEveryColumnOfAFullyPopulatedRowAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coord(ctx);
    var instance = Guid.CreateVersion7();
    var stream = Guid.CreateVersion7();
    var ids = await _seedStreamAsync(conn, stream, 2);
    _ = await coordinator.ClaimWorkAsync(_request(instance, maxStreams: 1, outboxRows: 1, run: 1));

    // The second row is an event-store-only event (no destination, no partition) with a stamped,
    // forwarded event-store row, a scope, an envelope type and an error: every column the other test
    // reads as NULL is populated here, and the two it reads as populated are NULL.
    await using (var setup = conn.CreateCommand()) {
      setup.CommandText = @"
        UPDATE wh_outbox SET is_event = TRUE, scope = '{""t"":1}'::jsonb, envelope_type = 'Env', error = 'earlier failure',
                             destination = NULL, partition_number = NULL
        WHERE message_id = @id;
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at,
                                    commit_sequence, origin_service_id, origin_commit_sequence)
        VALUES (@id, @stream, @stream, 'Test', 'Test', NULL, 1, NOW(), 42, @origin, 7);";
      setup.Parameters.AddWithValue("id", ids[1]);
      setup.Parameters.AddWithValue("stream", stream);
      setup.Parameters.AddWithValue("origin", Guid.CreateVersion7());
      await setup.ExecuteNonQueryAsync();
    }

    var rows = await coordinator.ContinueOutboxStreamsAsync(
      [new OutboxStreamCursor(stream, ids[0])], instance, runLength: 10, maxBytes: 1_000_000);

    var row = rows.Single();
    await Assert.That(row.MessageId).IsEqualTo(ids[1]);
    await Assert.That(row.IsEvent).IsTrue();
    await Assert.That(row.Scope).IsNotNull();
    await Assert.That(row.EnvelopeType).IsEqualTo("Env");
    await Assert.That(row.Error).IsEqualTo("earlier failure");
    await Assert.That(row.CommitSequence).IsEqualTo(42L);
    await Assert.That(row.OriginServiceId).IsNotNull();
    await Assert.That(row.OriginCommitSequence).IsEqualTo(7L);
    await Assert.That(row.Destination).IsNull();
    await Assert.That(row.PartitionNumber).IsNull();
  }
}
