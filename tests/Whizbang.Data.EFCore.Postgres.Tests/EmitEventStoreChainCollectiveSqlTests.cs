using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// SQL-level integration coverage for the collective-event routing branch of
/// <c>_emit_event_store_chain</c> (migration 061).
///
/// <para>
/// A collective event (<c>EventFlags.Collective = 1</c>, i.e. <c>(flags &amp; 1) = 1</c>) carries no
/// per-stream perspective association — its semantics are "apply this mutation to every row in scope".
/// The chain must therefore route it to a single fixed sink perspective name
/// (<c>__collective__</c>) so the perspective worker can hand it to <c>ICollectiveDispatcher</c>
/// exactly once, regardless of how many model handlers subscribe. Two facts are pinned here:
/// </para>
/// <list type="number">
///   <item><description>The <c>flags</c> column is carried from <c>wh_outbox</c> into
///   <c>wh_event_store</c> (it was previously dropped, leaving <c>wh_event_store.flags</c> stuck at 0).</description></item>
///   <item><description>Exactly one <c>wh_perspective_events</c> row, <c>perspective_name='__collective__'</c>,
///   is created per collective event — driven purely by the flag, with no association lookup.</description></item>
/// </list>
/// </summary>
[Category("Shard2")]
public class EmitEventStoreChainCollectiveSqlTests : EFCoreTestBase {

  // Mirror of the SQL literal in migration 061. Kept in sync with the C# sink constant the
  // perspective-worker seam consumes (Slice 2). The SQL/C# boundary forces this single duplication.
  private const string COLLECTIVE_SINK = "__collective__";
  private const string COLLECTIVE_EVENT_TYPE = "Whizbang.Tests.ArchiveJobsCollectiveEvent, Whizbang.Tests";
  private const string NORMAL_EVENT_TYPE = "Whizbang.Tests.OrdinaryEvent, Whizbang.Tests";
  private const string PERSPECTIVE_ALPHA = "Test.PerspectiveAlpha+Projection";

  private static async Task _insertOutboxEventAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, string eventType, Guid instanceId, int flags) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event,
         flags, created_at)
      VALUES
        (@id, 'test-dest', @type, 'env', '{"p":{}}'::jsonb, '{}'::jsonb,
         1, 0, @inst, NOW() + INTERVAL '5 minutes', 0, @sid, true, @flags, NOW())
      """;
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue("type", eventType);
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("flags", flags);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _registerAssociationAsync(
      NpgsqlConnection conn, string eventType, string perspectiveName, string serviceName = "test-service") {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name,
         normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @messageType, 'perspective', @target, @service,
              @messageType, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """;
    cmd.Parameters.AddWithValue("messageType", eventType);
    cmd.Parameters.AddWithValue("target", perspectiveName);
    cmd.Parameters.AddWithValue("service", serviceName);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _callEmitEventStoreChainAsync(NpgsqlConnection conn, Guid[] outboxIds, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    cmd.Parameters.AddWithValue("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, outboxIds);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _countPerspectiveEventsAsync(
      NpgsqlConnection conn, Guid eventId, string? perspectiveName = null) {
    await using var cmd = conn.CreateCommand();
    if (perspectiveName is null) {
      cmd.CommandText = "SELECT count(*) FROM wh_perspective_events WHERE event_id = @eid";
      cmd.Parameters.AddWithValue("eid", eventId);
    } else {
      cmd.CommandText = "SELECT count(*) FROM wh_perspective_events WHERE event_id = @eid AND perspective_name = @pn";
      cmd.Parameters.AddWithValue("eid", eventId);
      cmd.Parameters.AddWithValue("pn", perspectiveName);
    }
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<int> _eventStoreFlagsAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT flags FROM wh_event_store WHERE event_id = @eid";
    cmd.Parameters.AddWithValue("eid", eventId);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  // Calls the real store_outbox_messages proc (migration 062) with a collective-event message whose Flags
  // field is rendered as <paramref name="flagsJson"/> (a JSON number like 1, or a string like "Collective"
  // — both shapes EventFlags can serialize to). Returns the wh_outbox.flags the proc persisted.
  private static async Task<int> _storeOutboxThenFlagsAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, string eventType, Guid instanceId, string flagsJson) {
    var msgs = "[{\"MessageId\":\"" + messageId + "\",\"Destination\":\"test-dest\",\"MessageType\":\"" + eventType
      + "\",\"EnvelopeType\":\"env\",\"Envelope\":{\"p\":{}},\"Metadata\":{},\"Scope\":null,\"StreamId\":\""
      + streamId + "\",\"IsEvent\":true,\"Flags\":" + flagsJson + "}]";
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT * FROM store_outbox_messages(@msgs::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
      cmd.Parameters.AddWithValue("msgs", msgs);
      cmd.Parameters.AddWithValue("inst", instanceId);
      await cmd.ExecuteNonQueryAsync();
    }
    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT flags FROM wh_outbox WHERE message_id = @id";
    read.Parameters.AddWithValue("id", messageId);
    return (int)(await read.ExecuteScalarAsync())!;
  }

  // ---------- TESTS ----------

  [Test]
  public async Task EmitEventStoreChain_CollectiveEvent_CreatesSingleCollectiveSinkRowAsync() {
    // A collective event (flags & 1 = 1) with NO perspective association must still produce
    // exactly one wh_perspective_events row, routed to the fixed __collective__ sink — the
    // single work item the perspective worker dispatches via ICollectiveDispatcher.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    await _insertOutboxEventAsync(conn, messageId, streamId, COLLECTIVE_EVENT_TYPE, instanceId, flags: 1);
    await _callEmitEventStoreChainAsync(conn, [messageId], instanceId);

    var sinkCount = await _countPerspectiveEventsAsync(conn, messageId, COLLECTIVE_SINK);
    var totalCount = await _countPerspectiveEventsAsync(conn, messageId);

    await Assert.That(sinkCount).IsEqualTo(1L)
      .Because("A collective event must create exactly one __collective__ work row so the perspective worker dispatches it once.");
    await Assert.That(totalCount).IsEqualTo(1L)
      .Because("No perspective association exists for the collective event — the ONLY row should be the __collective__ sink. More than one would mean duplicate DispatchAsync calls.");
  }

  [Test]
  public async Task EmitEventStoreChain_CollectiveEvent_CarriesFlagsIntoEventStoreAsync() {
    // The flags column must be carried from wh_outbox into wh_event_store. Before migration 061
    // it was dropped on the copy, leaving wh_event_store.flags = 0 — so the documented
    // `WHERE (flags & 1) = 1` routing predicate could never match.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    await _insertOutboxEventAsync(conn, messageId, streamId, COLLECTIVE_EVENT_TYPE, instanceId, flags: 1);
    await _callEmitEventStoreChainAsync(conn, [messageId], instanceId);

    var storedFlags = await _eventStoreFlagsAsync(conn, messageId);

    await Assert.That(storedFlags).IsEqualTo(1)
      .Because("EventFlags.Collective (1) must survive the outbox→event_store copy so the collective routing predicate matches.");
  }

  [Test]
  public async Task StoreOutboxMessages_CollectiveEvent_PersistsFlagsAndRoutesToSinkAsync() {
    // The end-to-end gap migration 062 closes: store_outbox_messages (migrations 020/021) never wrote the
    // `flags` column added in 061, so a collective event published through the real proc landed with
    // flags=0 → 061's (flags & 1) routing never matched → no __collective__ sink row → never applied.
    // The existing tests above INSERT into wh_outbox directly (bypassing the proc), which is exactly why
    // this gap slipped through. Drive the real proc here.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    // EventFlags.Collective as a JSON number (1) — the default System.Text.Json enum serialization.
    var outboxFlags = await _storeOutboxThenFlagsAsync(conn, messageId, streamId, COLLECTIVE_EVENT_TYPE, instanceId, "1");

    await Assert.That(outboxFlags).IsEqualTo(1)
      .Because("store_outbox_messages must persist the EventFlags flags column.");
    await Assert.That(await _eventStoreFlagsAsync(conn, messageId)).IsEqualTo(1)
      .Because("The proc's _emit_event_store_chain call must carry the persisted flag into wh_event_store.");
    await Assert.That(await _countPerspectiveEventsAsync(conn, messageId, COLLECTIVE_SINK)).IsEqualTo(1L)
      .Because("With flags persisted, the collective event routes to exactly one __collective__ sink row.");
  }

  [Test]
  public async Task StoreOutboxMessages_CollectiveEvent_PersistsFlagsFromStringEnumAsync() {
    // EventFlags can also serialize as a [Flags] string ("Collective") when the caller's options carry a
    // JsonStringEnumConverter. The proc's reader must handle that shape too.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    var outboxFlags = await _storeOutboxThenFlagsAsync(conn, messageId, streamId, COLLECTIVE_EVENT_TYPE, instanceId, "\"Collective\"");

    await Assert.That(outboxFlags).IsEqualTo(1)
      .Because("A string-serialized EventFlags ('Collective') must still set the collective bit on wh_outbox.flags.");
  }

  [Test]
  public async Task EmitEventStoreChain_OrdinaryEvent_CreatesNoCollectiveSinkRowAsync() {
    // Guard the converse: an ordinary (flags = 0) event with a normal perspective association
    // must get its normal perspective_events row and ZERO __collective__ rows.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    await _registerAssociationAsync(conn, NORMAL_EVENT_TYPE, PERSPECTIVE_ALPHA);
    await _insertOutboxEventAsync(conn, messageId, streamId, NORMAL_EVENT_TYPE, instanceId, flags: 0);
    await _callEmitEventStoreChainAsync(conn, [messageId], instanceId);

    var sinkCount = await _countPerspectiveEventsAsync(conn, messageId, COLLECTIVE_SINK);
    var alphaCount = await _countPerspectiveEventsAsync(conn, messageId, PERSPECTIVE_ALPHA);

    await Assert.That(sinkCount).IsEqualTo(0L)
      .Because("A non-collective event must never be routed to the __collective__ sink.");
    await Assert.That(alphaCount).IsEqualTo(1L)
      .Because("The ordinary event still fans out to its registered perspective unchanged.");
  }
}
