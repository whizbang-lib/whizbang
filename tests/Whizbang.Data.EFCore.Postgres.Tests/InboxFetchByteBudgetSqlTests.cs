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
/// The drain fetch's BYTE budget (migration 091), against real Postgres.
///
/// <para>
/// <c>p_max_per_stream</c> bounds the slice by rows, which silently assumes rows are roughly the
/// same size. Control-plane traffic breaks that: an integrity manifest can dwarf an ordinary
/// command by orders of magnitude, so "fetch 100 rows" becomes tens of megabytes in one round trip
/// and several times that once deserialized. Observed live, that made the fetch meant to make
/// progress the thing that OOMKilled the process, so the backlog was redelivered and never drained.
/// </para>
///
/// <para>
/// The invariant that makes the budget safe is the one worth pinning: it trims the TAIL of a
/// slice, never the head. A message bigger than the whole budget must still come back on its own,
/// or its stream stalls forever and the bound has traded an OOM for a deadlock.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
[Category("Integration")]
[Category("Shard3")]
public class InboxFetchByteBudgetSqlTests : EFCoreTestBase {

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>Seeds one leased inbox row whose event_data is <paramref name="payloadBytes"/> long.</summary>
  private static async Task _seedAsync(NpgsqlConnection conn, Guid streamId, Guid messageId,
                                       Guid instanceId, int payloadBytes) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata,
                            stream_id, is_event, status, attempts, instance_id, lease_expiry,
                            source_service_id)
      VALUES (@id, 'h', 'T, A',
              jsonb_build_object('p', repeat('x', @bytes)), '{}'::jsonb,
              @stream, true, 1, 0, @inst, NOW() + interval '5 minutes',
              '00000000-0000-0000-0000-000000000001')
      """;
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue("bytes", payloadBytes);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static Guid _seq(int n) => Guid.Parse($"019f0000-0000-7000-8000-{n:D12}");

  [Test]
  public async Task ByteBudget_TrimsTheTailOfTheSlice_KeepingStreamOrderAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var stream = Guid.Parse("0a0a0a0a-1111-4111-8111-111111111111");
    var instance = Guid.Parse("0b0b0b0b-1111-4111-8111-111111111111");

    // Five rows of ~10 KB each. A 25 KB budget admits the first two and cuts the rest.
    for (var i = 1; i <= 5; i++) {
      await _seedAsync(conn, stream, _seq(i), instance, 10_000);
    }

    var all = await coordinator.FetchInboxBatchAsync([stream], instance, maxPerStream: 100);
    await Assert.That(all.Count).IsEqualTo(5)
      .Because("precondition: without a budget the count bound returns everything");

    var budgeted = await coordinator.FetchInboxBatchAsync([stream], instance, maxPerStream: 100, maxBytes: 25_000);
    await Assert.That(budgeted.Count).IsLessThan(5)
      .Because("the budget has to actually cut something, or it is decoration");
    await Assert.That(budgeted.Count).IsGreaterThanOrEqualTo(1);

    // The kept rows must be the FIRST ones — a budget that dropped from the head would reorder
    // the stream, and stream-FIFO is the invariant the whole drain path is built on.
    var expectedPrefix = all.Take(budgeted.Count).Select(r => r.MessageId).ToList();
    await Assert.That(budgeted.Select(r => r.MessageId).ToList()).IsEquivalentTo(expectedPrefix)
      .Because("the budget trims the tail; taking from the middle or end would break stream order");
  }

  [Test]
  public async Task ByteBudget_AlwaysReturnsAtLeastOneRow_EvenWhenItAloneExceedsTheBudgetAsync() {
    // The deadlock this avoids: a message larger than the budget would never be returned, so it
    // could never be processed, so it would block its stream forever. Trading an OOM for a stall
    // is not a fix.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var stream = Guid.Parse("0c0c0c0c-2222-4222-8222-222222222222");
    var instance = Guid.Parse("0d0d0d0d-2222-4222-8222-222222222222");

    await _seedAsync(conn, stream, _seq(101), instance, 50_000);   // alone, far over budget
    await _seedAsync(conn, stream, _seq(102), instance, 50_000);

    var rows = await coordinator.FetchInboxBatchAsync([stream], instance, maxPerStream: 100, maxBytes: 1_000);

    await Assert.That(rows.Count).IsEqualTo(1)
      .Because("the head row comes back regardless of size — otherwise the stream can never drain");
    await Assert.That(rows[0].MessageId).IsEqualTo(_seq(101))
      .Because("and it must be the FIRST row, not an arbitrary one");
  }

  [Test]
  public async Task NoBudget_BehavesExactlyAsBeforeAsync() {
    // Additive by default: every caller that has not opted in must see the old behavior, since
    // the migration replaces a function the whole drain path depends on.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var stream = Guid.Parse("0e0e0e0e-3333-4333-8333-333333333333");
    var instance = Guid.Parse("0f0f0f0f-3333-4333-8333-333333333333");

    for (var i = 1; i <= 4; i++) {
      await _seedAsync(conn, stream, _seq(200 + i), instance, 30_000);
    }

    var rows = await coordinator.FetchInboxBatchAsync([stream], instance, maxPerStream: 100, maxBytes: null);
    await Assert.That(rows.Count).IsEqualTo(4)
      .Because("null budget means count-bound only — 120 KB comes back exactly as it used to");
  }

  [Test]
  public async Task ByteBudget_IsPerStream_NotSharedAcrossStreamsAsync() {
    // The running total partitions by stream, so one busy stream cannot starve another in the
    // same multi-stream fetch — that would make drain progress depend on unrelated traffic.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var a = Guid.Parse("1a1a1a1a-4444-4444-8444-444444444444");
    var b = Guid.Parse("1b1b1b1b-4444-4444-8444-444444444444");
    var instance = Guid.Parse("1c1c1c1c-4444-4444-8444-444444444444");

    await _seedAsync(conn, a, _seq(301), instance, 10_000);
    await _seedAsync(conn, a, _seq(302), instance, 10_000);
    await _seedAsync(conn, b, _seq(303), instance, 10_000);
    await _seedAsync(conn, b, _seq(304), instance, 10_000);

    var rows = await coordinator.FetchInboxBatchAsync([a, b], instance, maxPerStream: 100, maxBytes: 12_000);

    await Assert.That(rows.Count(r => r.StreamId == a)).IsGreaterThanOrEqualTo(1);
    await Assert.That(rows.Count(r => r.StreamId == b)).IsGreaterThanOrEqualTo(1)
      .Because("each stream gets its own budget; a shared one would let one stream starve another");
  }
}
