using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// SQL-level integration coverage for <c>_emit_event_store_chain</c> when MULTIPLE
/// perspectives are registered for the same event type.
///
/// <para>
/// A production symptom observed on a consumer: <c>OrderLineRowAddedEvent</c> arrived in
/// the app-service inbox. <c>wh_message_associations</c> had TWO rows for that event
/// (Order+Projection AND OrderLines+Projection). After processing, only Order's
/// <c>wh_per_*</c> row was populated; OrderLines' table was empty for the saga's
/// order. The frontend canvas's per-field tag dispatcher fired
/// <c>LoadOrderLinesByOrder</c> but the API returned blank because the row
/// wasn't there.
/// </para>
///
/// <para>
/// These tests pin the contract that <c>_emit_event_store_chain</c> must create exactly
/// one <c>wh_perspective_events</c> row per registered perspective per event — symmetrically.
/// If the chain skips one perspective (DISTINCT collapse, JOIN bug, conflict-skip race), the
/// asymmetry shows up as exactly the production symptom.
/// </para>
/// </summary>
[Category("Shard4")]
public class EmitEventStoreChainMultiPerspectiveSqlTests : EFCoreTestBase {

  private const string EVENT_TYPE = "Whizbang.Tests.MultiPerspectiveEvent, Whizbang.Tests";
  private const string PERSPECTIVE_ALPHA = "Test.PerspectiveAlpha+Projection";
  private const string PERSPECTIVE_BETA = "Test.PerspectiveBeta+Projection";

  private static async Task _registerAssociationAsync(NpgsqlConnection conn, string eventType, string perspectiveName, string serviceName = "test-service") {
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

  private static async Task _insertOutboxEventAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, string eventType, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event,
         created_at)
      VALUES
        (@id, 'test-dest', @type, 'env', '{"p":{}}'::jsonb, '{}'::jsonb,
         1, 0, @inst, NOW() + INTERVAL '5 minutes', 0, @sid, true, NOW())
      """;
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue("type", eventType);
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("sid", streamId);
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

  // ---------- TESTS ----------

  [Test]
  public async Task EmitEventStoreChain_TwoPerspectivesRegistered_CreatesPerspectiveEventForEachAsync() {
    // Core symmetry contract: when N perspectives are associated with the event_type,
    // _emit_event_store_chain must create N wh_perspective_events rows. Asymmetric
    // creation here is exactly the production symptom: Order's row created,
    // OrderLines' row missing, despite both perspectives being registered.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    await _registerAssociationAsync(conn, EVENT_TYPE, PERSPECTIVE_ALPHA);
    await _registerAssociationAsync(conn, EVENT_TYPE, PERSPECTIVE_BETA);
    await _insertOutboxEventAsync(conn, messageId, streamId, EVENT_TYPE, instanceId);
    await _callEmitEventStoreChainAsync(conn, [messageId], instanceId);

    var alphaCount = await _countPerspectiveEventsAsync(conn, messageId, PERSPECTIVE_ALPHA);
    var betaCount = await _countPerspectiveEventsAsync(conn, messageId, PERSPECTIVE_BETA);
    var totalCount = await _countPerspectiveEventsAsync(conn, messageId);

    await Assert.That(alphaCount).IsEqualTo(1L)
      .Because("PerspectiveAlpha is registered for this event — must get exactly one wh_perspective_events row.");
    await Assert.That(betaCount).IsEqualTo(1L)
      .Because("PerspectiveBeta is also registered — must get exactly one wh_perspective_events row, symmetric with PerspectiveAlpha. Asymmetry here is the production symptom described above.");
    await Assert.That(totalCount).IsEqualTo(2L)
      .Because("Two perspectives × one event = two perspective_events rows. Anything else is a regression in the JOIN/INSERT logic of _emit_event_store_chain.");
  }

  [Test]
  public async Task EmitEventStoreChain_TwoPerspectivesAndMultipleEvents_SymmetricFanOutAsync() {
    // Multi-event extension: with N perspectives and M events of the same type, we must
    // get N×M perspective_events rows. Each event must reach BOTH perspectives.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var event1 = Guid.NewGuid();
    var event2 = Guid.NewGuid();
    var event3 = Guid.NewGuid();

    await _registerAssociationAsync(conn, EVENT_TYPE, PERSPECTIVE_ALPHA);
    await _registerAssociationAsync(conn, EVENT_TYPE, PERSPECTIVE_BETA);
    await _insertOutboxEventAsync(conn, event1, streamId, EVENT_TYPE, instanceId);
    await _insertOutboxEventAsync(conn, event2, streamId, EVENT_TYPE, instanceId);
    await _insertOutboxEventAsync(conn, event3, streamId, EVENT_TYPE, instanceId);
    await _callEmitEventStoreChainAsync(conn, [event1, event2, event3], instanceId);

    foreach (var eid in new[] { event1, event2, event3 }) {
      var alphaCount = await _countPerspectiveEventsAsync(conn, eid, PERSPECTIVE_ALPHA);
      var betaCount = await _countPerspectiveEventsAsync(conn, eid, PERSPECTIVE_BETA);
      await Assert.That(alphaCount).IsEqualTo(1L);
      await Assert.That(betaCount).IsEqualTo(1L);
    }

    await using var totalCmd = conn.CreateCommand();
    totalCmd.CommandText = "SELECT count(*) FROM wh_perspective_events WHERE event_id = ANY(@ids)";
    totalCmd.Parameters.AddWithValue("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, new[] { event1, event2, event3 });
    var total = (long)(await totalCmd.ExecuteScalarAsync())!;
    await Assert.That(total).IsEqualTo(6L)
      .Because("Three events × two perspectives = six perspective_events rows. Anything less means an event/perspective combination was dropped.");
  }

  [Test]
  public async Task EmitEventStoreChain_OnePerspectiveRegistered_CreatesOneRowAsync() {
    // Baseline: with exactly one perspective registered, exactly one row should appear.
    // Pins the count for a 1-perspective scenario so a regression that DROPS rows
    // (rather than ADDS) would catch here.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    const string singletonEventType = "Whizbang.Tests.SinglePerspectiveEvent, Whizbang.Tests";

    await _registerAssociationAsync(conn, singletonEventType, PERSPECTIVE_ALPHA);
    await _insertOutboxEventAsync(conn, messageId, streamId, singletonEventType, instanceId);
    await _callEmitEventStoreChainAsync(conn, [messageId], instanceId);

    var alphaCount = await _countPerspectiveEventsAsync(conn, messageId, PERSPECTIVE_ALPHA);
    var totalCount = await _countPerspectiveEventsAsync(conn, messageId);

    await Assert.That(alphaCount).IsEqualTo(1L);
    await Assert.That(totalCount).IsEqualTo(1L)
      .Because("Single perspective registered — only one row should appear.");
  }

  [Test]
  public async Task EmitEventStoreChain_PerspectiveRegisteredForDifferentService_DoesNotCreatePerspectiveEventAsync() {
    // service_name on wh_message_associations is for diagnostics — the chain joins
    // purely on message_type, not service_name. A perspective registered in another service
    // should still get a row when an event of its type passes through (because in shared-DB
    // setups, multiple services' perspectives all live in the same wh_per_* tables).
    //
    // This test pins the actual behavior so a future "filter by service_name" change is
    // caught — flipping that filter would silently drop perspective_events for other services
    // sharing the DB.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    const string crossServiceEventType = "Whizbang.Tests.CrossServiceEvent, Whizbang.Tests";

    // Perspective Alpha registered under "ServiceA"
    await _registerAssociationAsync(conn, crossServiceEventType, PERSPECTIVE_ALPHA, serviceName: "ServiceA");
    // Perspective Beta registered under "ServiceB"
    await _registerAssociationAsync(conn, crossServiceEventType, PERSPECTIVE_BETA, serviceName: "ServiceB");

    await _insertOutboxEventAsync(conn, messageId, streamId, crossServiceEventType, instanceId);
    await _callEmitEventStoreChainAsync(conn, [messageId], instanceId);

    var alphaCount = await _countPerspectiveEventsAsync(conn, messageId, PERSPECTIVE_ALPHA);
    var betaCount = await _countPerspectiveEventsAsync(conn, messageId, PERSPECTIVE_BETA);

    // Current observed behavior: chain creates rows for BOTH regardless of service_name.
    // If a consumer expects service-scoped filtering, that filter must be added carefully —
    // the assumption today is shared DB ⇒ all perspectives apply.
    await Assert.That(alphaCount).IsEqualTo(1L);
    await Assert.That(betaCount).IsEqualTo(1L);
  }
}
