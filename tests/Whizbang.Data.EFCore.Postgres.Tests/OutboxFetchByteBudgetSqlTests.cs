using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The OUTBOX drain fetch's byte budget (migration 096) — the sibling of the inbox bound (091),
/// against real Postgres. Same defect, other direction: an origin serving a storm of queued
/// redelivery requests drains its outbox in count-bounded slices, and control-plane rows
/// (composites carrying whole event pages) dwarf ordinary commands by orders of magnitude —
/// "fetch 100 rows" becomes tens of megabytes per round trip, several times that on the heap,
/// per drain consumer. Observed live as an origin OOM-looping THROUGH an 8 GB limit while
/// productively serving backfill.
///
/// <para>Same safety invariant as the inbox: the budget trims the TAIL of a slice and always
/// returns at least the head row, so an oversized message still ships instead of stalling its
/// stream forever.</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
[Category("Integration")]
public class OutboxFetchByteBudgetSqlTests : EFCoreTestBase {

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>Seeds one leased outbox row whose event_data is <paramref name="payloadBytes"/> long.</summary>
  private static async Task _seedAsync(NpgsqlConnection conn, Guid streamId, Guid messageId,
                                       Guid instanceId, int payloadBytes) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope, status,
         attempts, created_at, stream_id, partition_number, instance_id, lease_expiry, is_event)
      VALUES (@id, 'topic-x', 'T, A', 'E, A',
              ('{"p":"' || repeat('x', @bytes) || '"}')::jsonb, '{}'::jsonb, '{}'::jsonb, 3,
              0, NOW(), @stream, 7, @inst, NOW() + INTERVAL '5 minutes', false)
      """;
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue("bytes", payloadBytes);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static Guid _seq(int n) => Guid.Parse($"019f1111-0000-7000-8000-{n:D12}");

  [Test]
  public async Task ByteBudget_TrimsTheTailOfTheSlice_KeepingStreamOrderAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var stream = Guid.Parse("2a2a2a2a-1111-4111-8111-111111111111");
    var instance = Guid.Parse("2b2b2b2b-1111-4111-8111-111111111111");

    for (var i = 1; i <= 5; i++) {
      await _seedAsync(conn, stream, _seq(i), instance, 10_000);
    }

    var all = await coordinator.FetchOutboxBatchAsync([stream], instance, maxPerStream: 100);
    await Assert.That(all.Count).IsEqualTo(5)
      .Because("precondition: without a budget the count bound returns everything");

    var budgeted = await coordinator.FetchOutboxBatchAsync([stream], instance, maxPerStream: 100, maxBytes: 25_000);
    await Assert.That(budgeted.Count).IsLessThan(5)
      .Because("the budget has to actually cut something, or it is decoration");
    await Assert.That(budgeted.Count).IsGreaterThanOrEqualTo(1);

    var expectedPrefix = all.Take(budgeted.Count).Select(r => r.MessageId).ToList();
    await Assert.That(budgeted.Select(r => r.MessageId).ToList()).IsEquivalentTo(expectedPrefix)
      .Because("the budget trims the tail; taking from the middle or end would break publish order");
  }

  [Test]
  public async Task ByteBudget_AlwaysReturnsAtLeastOneRow_EvenWhenItAloneExceedsTheBudgetAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var stream = Guid.Parse("2c2c2c2c-2222-4222-8222-222222222222");
    var instance = Guid.Parse("2d2d2d2d-2222-4222-8222-222222222222");

    await _seedAsync(conn, stream, _seq(101), instance, 50_000);
    await _seedAsync(conn, stream, _seq(102), instance, 50_000);

    var rows = await coordinator.FetchOutboxBatchAsync([stream], instance, maxPerStream: 100, maxBytes: 1_000);

    await Assert.That(rows.Count).IsEqualTo(1)
      .Because("the head row publishes regardless of size — otherwise the stream can never drain");
    await Assert.That(rows[0].MessageId).IsEqualTo(_seq(101));
  }

  [Test]
  public async Task NoBudget_BehavesExactlyAsBeforeAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var stream = Guid.Parse("2e2e2e2e-3333-4333-8333-333333333333");
    var instance = Guid.Parse("2f2f2f2f-3333-4333-8333-333333333333");

    for (var i = 1; i <= 4; i++) {
      await _seedAsync(conn, stream, _seq(200 + i), instance, 30_000);
    }

    var rows = await coordinator.FetchOutboxBatchAsync([stream], instance, maxPerStream: 100, maxBytes: null);
    await Assert.That(rows.Count).IsEqualTo(4)
      .Because("null budget means count-bound only — the migration must be additive for non-opted callers");
  }

  [Test]
  public async Task ByteBudget_IsPerStream_NotSharedAcrossStreamsAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var a = Guid.Parse("3a3a3a3a-4444-4444-8444-444444444444");
    var b = Guid.Parse("3b3b3b3b-4444-4444-8444-444444444444");
    var instance = Guid.Parse("3c3c3c3c-4444-4444-8444-444444444444");

    await _seedAsync(conn, a, _seq(301), instance, 10_000);
    await _seedAsync(conn, a, _seq(302), instance, 10_000);
    await _seedAsync(conn, b, _seq(303), instance, 10_000);
    await _seedAsync(conn, b, _seq(304), instance, 10_000);

    var rows = await coordinator.FetchOutboxBatchAsync([a, b], instance, maxPerStream: 100, maxBytes: 12_000);

    await Assert.That(rows.Count(r => r.StreamId == a)).IsGreaterThanOrEqualTo(1);
    await Assert.That(rows.Count(r => r.StreamId == b)).IsGreaterThanOrEqualTo(1)
      .Because("each stream gets its own budget; a shared one would let one stream starve another");
  }
}
