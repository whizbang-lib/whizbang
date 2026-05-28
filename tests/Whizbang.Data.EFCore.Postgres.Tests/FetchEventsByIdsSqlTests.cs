using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <c>fetch_events_by_ids</c> — Phase H step 7 slice 4. Scoped body fetch
/// from <c>wh_event_store</c> by event_id list. Replaces <c>get_stream_events</c>'s
/// per-stream JOIN with a precise lookup so the drainer fetches bodies only for events
/// that survived the cooldown + cursor + inversion filters.
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public class FetchEventsByIdsSqlTests : EFCoreTestBase {

  [Test]
  public async Task FetchEventsByIds_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname='fetch_events_by_ids' AND pronamespace='public'::regnamespace);";
    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task FetchEventsByIds_ReturnsRowsForGivenIds_OrderedByEventIdAscAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var streamId = (Guid)TrackedGuid.NewMedo();
    var event1 = (Guid)TrackedGuid.NewMedo();
    await Task.Delay(2);
    var event2 = (Guid)TrackedGuid.NewMedo();
    await Task.Delay(2);
    var event3 = (Guid)TrackedGuid.NewMedo();

    // Insert event_store rows in reverse to prove ordering by event_id, not insert order.
    await _insertEventStoreRowAsync(connection, event3, streamId, "TypeC", "{\"v\":3}", version: 3);
    await _insertEventStoreRowAsync(connection, event1, streamId, "TypeA", "{\"v\":1}", version: 1);
    await _insertEventStoreRowAsync(connection, event2, streamId, "TypeB", "{\"v\":2}", version: 2);

    var rows = await _fetchAsync(connection, [event1, event2, event3]);

    await Assert.That(rows.Count).IsEqualTo(3);
    await Assert.That(rows[0].EventId).IsEqualTo(event1);
    await Assert.That(rows[0].EventType).IsEqualTo("TypeA");
    await Assert.That(rows[1].EventId).IsEqualTo(event2);
    await Assert.That(rows[2].EventId).IsEqualTo(event3);
  }

  [Test]
  public async Task FetchEventsByIds_UnknownIds_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var rows = await _fetchAsync(connection, [(Guid)TrackedGuid.NewMedo(), (Guid)TrackedGuid.NewMedo()]);

    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchEventsByIds_PartialMatch_ReturnsOnlyKnownAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var streamId = (Guid)TrackedGuid.NewMedo();
    var existing = (Guid)TrackedGuid.NewMedo();
    var missing = (Guid)TrackedGuid.NewMedo();
    await _insertEventStoreRowAsync(connection, existing, streamId, "Type", "{\"v\":1}");

    var rows = await _fetchAsync(connection, [existing, missing]);

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].EventId).IsEqualTo(existing);
  }

  [Test]
  public async Task FetchEventsByIds_RoundTripsAllColumnsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _insertEventStoreRowAsync(connection, eventId, streamId, "TestType",
      eventData: "{\"payload\":42}",
      metadata: "{\"hop\":1}",
      scope: "{\"tenant\":\"t1\"}");

    var rows = await _fetchAsync(connection, [eventId]);

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].StreamId).IsEqualTo(streamId);
    await Assert.That(rows[0].EventId).IsEqualTo(eventId);
    await Assert.That(rows[0].EventType).IsEqualTo("TestType");
    await Assert.That(rows[0].EventData).Contains("\"payload\"");
    await Assert.That(rows[0].Metadata).IsNotNull();
    await Assert.That(rows[0].Metadata!).Contains("\"hop\"");
    await Assert.That(rows[0].Scope).IsNotNull();
    await Assert.That(rows[0].Scope!).Contains("\"tenant\"");
  }

  // --- helpers ---

  private static async Task<List<Row>> _fetchAsync(NpgsqlConnection connection, Guid[] eventIds) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT * FROM fetch_events_by_ids(@p_event_ids)";
    cmd.Parameters.Add(new NpgsqlParameter("p_event_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = eventIds });

    var rows = new List<Row>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new Row {
        StreamId = reader.GetGuid(0),
        EventId = reader.GetGuid(1),
        EventType = reader.GetString(2),
        EventData = reader.GetString(3),
        Metadata = await reader.IsDBNullAsync(4) ? null : reader.GetString(4),
        Scope = await reader.IsDBNullAsync(5) ? null : reader.GetString(5)
      });
    }
    return rows;
  }

  private sealed class Row {
    public Guid StreamId { get; init; }
    public Guid EventId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string EventData { get; init; } = string.Empty;
    public string? Metadata { get; init; }
    public string? Scope { get; init; }
  }

  private static async Task _insertEventStoreRowAsync(
      NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType,
      string eventData, string metadata = "{}", string scope = "{}", int version = 1) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, scope, version, created_at)
      VALUES (@evt, @stream, @stream, 'agg', @type, @data::jsonb, @meta::jsonb, @scope::jsonb, @ver, NOW())";
    ins.Parameters.AddWithValue("evt", eventId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("type", eventType);
    ins.Parameters.AddWithValue("data", eventData);
    ins.Parameters.AddWithValue("meta", metadata);
    ins.Parameters.AddWithValue("scope", scope);
    ins.Parameters.AddWithValue("ver", version);
    await ins.ExecuteNonQueryAsync();
  }
}
