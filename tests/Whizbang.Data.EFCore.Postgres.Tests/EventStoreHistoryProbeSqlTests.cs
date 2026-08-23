using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <c>EFCoreEventStore.HasStreamEventsBeforeAsync</c> — the
/// resurrection-on-wake history probe (perspective row retention). On the row-null branch of a
/// row-TTL Sourced perspective, the generated runner asks: does this stream hold events ordered
/// before the incoming batch? True means the row was reaped and the stream woke — re-fold via the
/// rewind core; false means a genuinely new stream — apply normally. Verified against a real
/// Postgres so the LINQ Guid comparison provably translates to the uuid ordering the store uses.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
[Category("Shard3")]
public class EventStoreHistoryProbeSqlTests : EFCoreTestBase {
  private static async Task _seedPointerAsync(NpgsqlConnection conn, Guid eventId, Guid streamId, long version) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at)
      VALUES (@event, @stream, @stream, 'TestAggregate', 'TestNamespace.ProbeEvent', 'null'::jsonb, @ver,
              nextval('wh_commit_seq'), 0, NOW() - INTERVAL '61 days')
      """;
    cmd.Parameters.AddWithValue("event", eventId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("ver", version);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task Probe_StreamWithOlderEvents_ReturnsTrueAsync() {
    await using var context = CreateDbContext();
    var conn = (NpgsqlConnection)context.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();
    var oldEvent = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-61));
    var wakingEvent = Guid.CreateVersion7();
    await _seedPointerAsync(conn, oldEvent, streamId, 1);

    await Assert.That(await store.HasStreamEventsBeforeAsync(streamId, wakingEvent)).IsTrue()
      .Because("a reaped stream's 61-day-old history orders before the waking event — resurrection required.");
  }

  [Test]
  public async Task Probe_NewStream_ReturnsFalseAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    await Assert.That(await store.HasStreamEventsBeforeAsync(Guid.NewGuid(), Guid.CreateVersion7())).IsFalse()
      .Because("a stream with no stored events is genuinely new — no resurrection.");
  }

  [Test]
  public async Task Probe_OnlyLaterEvents_ReturnsFalseAsync() {
    // The batch's own (or later) events never count as history — only strictly-earlier ids do.
    await using var context = CreateDbContext();
    var conn = (NpgsqlConnection)context.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();
    var probeAnchor = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-1));
    var laterEvent = Guid.CreateVersion7();
    await _seedPointerAsync(conn, laterEvent, streamId, 1);

    await Assert.That(await store.HasStreamEventsBeforeAsync(streamId, probeAnchor)).IsFalse()
      .Because("only events ordered strictly before the anchor are pre-batch history.");
  }
}
