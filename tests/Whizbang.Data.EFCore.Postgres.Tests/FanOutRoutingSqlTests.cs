// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2619: a fan-out event reaches the collective sink through the perspective association insert both emit chains
/// already run, with no SQL change: the startup step writes the fan-out type as a <c>perspective</c> association whose
/// target is <c>__collective__</c>. These cases pin every SQL path that treats the sink specially, for an event that
/// is not a collective: the collective gate (391), the inbox's collective branch (574), the rewind (600) and the
/// ephemeral reap's floor gate. The associations are written by <c>register_message_associations</c> from the JSON
/// <see cref="CollectiveRoutingAssociations.AssociationEntriesJson"/> builds, as the startup step writes them.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/CollectiveRoutingAssociations.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/573_StreamsAreVersionedInEmissionOrder.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/574_ReceivedStreamsAreVersionedInReceiveOrder.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/600_ARewindNeverReachesBelowTheReapFloor.sql</code-under-test>
/// <docs>fundamentals/messaging/collective-events#fan-out-rows</docs>
[Category("Integration")]
[Category("Shard2")]
public class FanOutRoutingSqlTests : EFCoreTestBase {
  private const string SINK = "__collective__";
  private const string SERVICE = "Orders.Service";
  private const string RENAMED = "Customers.CustomerRenamed, Customers";
  private const string PLACED = "Orders.OrderPlaced, Orders";
  private const string ORDERS = "Orders.OrderSummaryProjection";
  private const string COLLECTIVE = "Contracts.ArchiveJobsCollective, Contracts";

  [Test]
  public async Task Outbox_AFanOutEvent_GetsOneSinkRow_ThoughItCarriesNoCollectiveFlagAsync() {
    await using var conn = await _openAsync();
    await _registerAsync(conn, SERVICE, handled: null, fanOuts: [RENAMED]);

    var renamed = await _emitOutboxAsync(conn, RENAMED, flags: 0);

    await Assert.That(await _rowsAsync(conn, renamed, SINK)).IsEqualTo(1L);
  }

  [Test]
  public async Task Outbox_TwoPerspectivesAndTwoServicesFanningOutOneType_StillGetOneSinkRowAsync() {
    // One service with two fan-outs of the type writes it once; a second service in the schema writes its own row.
    await using var conn = await _openAsync();
    await _registerAsync(conn, SERVICE, handled: null, fanOuts: [RENAMED, RENAMED]);
    await _registerAsync(conn, "Invoices.Service", handled: null, fanOuts: [RENAMED]);

    var renamed = await _emitOutboxAsync(conn, RENAMED, flags: 0);

    await Assert.That(await _rowsAsync(conn, renamed, SINK)).IsEqualTo(1L)
      .Because("the sink dispatches each event once and fans it out to every model itself");
  }

  [Test]
  public async Task Outbox_AFanOutEvent_IsNotGatedByTheCollectiveGate_WhichGatesOnlyCollectivesAsync() {
    // 391: the service gates collectives and handles none, which would drop a collective's row. A fan-out row comes from
    // the association insert, never the flag branch, so the gate cannot drop it.
    await using var conn = await _openAsync();
    await _registerAsync(conn, SERVICE, handled: [], fanOuts: [RENAMED]);

    var renamed = await _emitOutboxAsync(conn, RENAMED, flags: 0);
    var collective = await _emitOutboxAsync(conn, COLLECTIVE, flags: 1);

    await Assert.That(await _rowsAsync(conn, renamed, SINK)).IsEqualTo(1L);
    await Assert.That(await _rowsAsync(conn, collective, SINK)).IsEqualTo(0L).Because("the control: the gate is on");
  }

  [Test]
  public async Task Outbox_AFanOutEventThatIsAlsoACollective_GetsOneSinkRowAsync() {
    await using var conn = await _openAsync();
    await _registerAsync(conn, SERVICE, handled: [COLLECTIVE], fanOuts: [COLLECTIVE]);

    var both = await _emitOutboxAsync(conn, COLLECTIVE, flags: 1);

    await Assert.That(await _rowsAsync(conn, both, SINK)).IsEqualTo(1L)
      .Because("the collective branch finds the row the association insert made and adds none");
  }

  [Test]
  public async Task Outbox_AnOrdinaryEvent_GetsItsPerspectiveRow_AndNoSinkRowAsync() {
    await using var conn = await _openAsync();
    await _registerAsync(conn, SERVICE, handled: [], fanOuts: [RENAMED]);

    var placed = await _emitOutboxAsync(conn, PLACED, flags: 0);

    await Assert.That(await _rowsAsync(conn, placed, ORDERS)).IsEqualTo(1L);
    await Assert.That(await _rowsAsync(conn, placed, SINK)).IsEqualTo(0L);
  }

  [Test]
  public async Task Outbox_AFanOutEventAPerspectiveAlsoReads_GetsOneRowForEachAsync() {
    // The foreign aggregate's own perspective reads the event as usual; the fan-out adds the sink row beside it.
    await using var conn = await _openAsync();
    await _registerAsync(conn, SERVICE, handled: null, fanOuts: [RENAMED], perspectiveTypes: [PLACED, RENAMED]);

    var renamed = await _emitOutboxAsync(conn, RENAMED, flags: 0);

    await Assert.That(await _rowsAsync(conn, renamed, ORDERS)).IsEqualTo(1L);
    await Assert.That(await _rowsAsync(conn, renamed, SINK)).IsEqualTo(1L);
  }

  [Test]
  public async Task Inbox_AFanOutEvent_GetsOneSinkRow_ThoughThePassStoredNoCollectiveAsync() {
    // 574: the inbox runs its collective branch only when an event the pass stored carries the flag. A fan-out row does
    // not depend on that branch.
    await using var conn = await _openAsync();
    await _registerAsync(conn, SERVICE, handled: [], fanOuts: [RENAMED]);
    var instance = Guid.NewGuid();
    var renamed = await _seedInboxAsync(conn, RENAMED, instance);

    await _emitInboxAsync(conn, instance);

    await Assert.That(await _rowsAsync(conn, renamed, SINK)).IsEqualTo(1L);
  }

  [Test]
  public async Task Inbox_AnOrdinaryEvent_GetsNoSinkRowAsync() {
    await using var conn = await _openAsync();
    await _registerAsync(conn, SERVICE, handled: [], fanOuts: [RENAMED]);
    var instance = Guid.NewGuid();
    var placed = await _seedInboxAsync(conn, PLACED, instance);

    await _emitInboxAsync(conn, instance);

    await Assert.That(await _rowsAsync(conn, placed, ORDERS)).IsEqualTo(1L);
    await Assert.That(await _rowsAsync(conn, placed, SINK)).IsEqualTo(0L);
  }

  [Test]
  public async Task Rewind_OfTheSinkFromAFanOutEvent_FlagsAndQueuesNothing_AsForACollectiveAsync() {
    // 600: the sink has no runner to replay with, so a rewind of it is skipped. For a collective the floor is NULL (no
    // perspective association names the sink); for a fan-out the floor is found, and the sink is still skipped.
    await using var conn = await _openAsync();
    await _registerAsync(conn, SERVICE, handled: [COLLECTIVE], fanOuts: [RENAMED]);
    var renamed = await _emitOutboxAsync(conn, RENAMED, flags: 0);
    var collective = await _emitOutboxAsync(conn, COLLECTIVE, flags: 1);

    foreach (var eventId in new[] { renamed, collective }) {
      var stream = await _streamOfAsync(conn, eventId);
      await _executeAsync(conn, "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_id = @e", ("e", eventId));
      await _executeAsync(conn,
        "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES (@s, @p, @e, 2)",
        ("s", stream), ("p", SINK), ("e", eventId));

      var flagged = await _scalarAsync<int>(conn,
        "SELECT _wh_rewind_perspectives_from(ARRAY[@s]::uuid[], ARRAY[@p]::text[], ARRAY[@e]::uuid[], NOW())",
        ("s", stream), ("p", SINK), ("e", eventId));

      await Assert.That(flagged).IsEqualTo(0);
      await Assert.That(await _scalarAsync<int>(conn,
        "SELECT status FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p", ("s", stream), ("p", SINK)))
        .IsEqualTo(2).Because("the sink cursor is never flagged RewindRequired");
      await Assert.That(await _scalarAsync<long>(conn,
        "SELECT count(*) FROM wh_perspective_events WHERE event_id = @e AND processed_at IS NULL", ("e", eventId)))
        .IsEqualTo(0L).Because("no replay of the sink is queued");
    }
  }

  [Test]
  public async Task Reap_AFanOutEphemeralBody_IsNotHeldForASnapshotTheSinkNeverTakesAsync() {
    // The ephemeral reap holds a body until each perspective that reads it has a snapshot covering it, unless that
    // perspective keeps no rewind floor. The sink keeps none, and the fan-out entries say so.
    await using var conn = await _openAsync();
    await _registerAsync(conn, SERVICE, handled: null, fanOuts: [RENAMED]);
    var renamed = await _consumedEphemeralFanOutAsync(conn);

    await _maintainAsync(conn);

    await Assert.That(await _bodiesAsync(conn, renamed)).IsEqualTo(0L);
  }

  [Test]
  public async Task Reap_WithoutTheSinksNoFloorMarker_AFanOutEphemeralBodyIsHeldAsync() {
    // The control: the same routing written without the marker holds the body forever, which is what the marker is for.
    await using var conn = await _openAsync();
    await _registerAsync(conn, SERVICE, handled: null, fanOuts: [RENAMED], withoutSinkFloorMarker: true);
    var renamed = await _consumedEphemeralFanOutAsync(conn);

    await _maintainAsync(conn);

    await Assert.That(await _bodiesAsync(conn, renamed)).IsEqualTo(1L);
  }

  // ---------- helpers ----------

  /// <summary>A connection to a schema with no association yet, so each case registers exactly the services it is about.</summary>
  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _executeAsync(conn, "DELETE FROM wh_message_associations");
    return conn;
  }

  /// <summary>
  /// What the startup step registers for a service: its perspective's association, then the collective and fan-out
  /// entries <see cref="CollectiveRoutingAssociations.AssociationEntriesJson"/> builds, in one call.
  /// </summary>
  private static Task _registerAsync(
      NpgsqlConnection conn, string service, IReadOnlyList<string>? handled, IReadOnlyList<string> fanOuts,
      IReadOnlyList<string>? perspectiveTypes = null, bool withoutSinkFloorMarker = false) {
    var perspective = string.Join(",", (perspectiveTypes ?? [PLACED]).Select(type =>
      $$"""{"MessageType":"{{type}}","AssociationType":"perspective","TargetName":"{{ORDERS}}","ServiceName":"{{service}}"}"""));
    var entries = CollectiveRoutingAssociations.AssociationEntriesJson(handled, service, fanOuts);
    if (withoutSinkFloorMarker) {
      entries = entries.Replace(EphemeralRewindFloor.NoFloorAssociationEntryJson(SINK, service), "", StringComparison.Ordinal);
    }
    var json = "[" + perspective + entries + "]";
    return _executeAsync(conn, "SELECT register_message_associations(@json::jsonb, @service::varchar)",
      ("json", json), ("service", service));
  }

  private static async Task<Guid> _emitOutboxAsync(NpgsqlConnection conn, string eventType, int flags) {
    var id = Guid.NewGuid();
    var instance = Guid.NewGuid();
    await _executeAsync(conn, """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
      VALUES
        (@id, 'test-dest', @type, 'env', '{"p":{}}'::jsonb, '{}'::jsonb,
         1, 0, @inst, NOW() + INTERVAL '5 minutes', 0, @sid, true, @flags, NOW())
      """, ("id", id), ("type", eventType), ("inst", instance), ("sid", Guid.NewGuid()), ("flags", flags));
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    emit.Parameters.AddWithValue("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, new[] { id });
    emit.Parameters.AddWithValue("inst", instance);
    await emit.ExecuteNonQueryAsync();
    return id;
  }

  /// <summary>One inbox event with no flags, leased to <paramref name="holder"/> and not yet through the store pass.</summary>
  private static async Task<Guid> _seedInboxAsync(NpgsqlConnection conn, string eventType, Guid holder) {
    var id = Guid.NewGuid();
    await _executeAsync(conn, """
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority, flags)
        VALUES (@id, 'H', @type, '{"p": {}}', '{}', NOW() - INTERVAL '1 minute', @sid, true, 150, 0)
        RETURNING message_id, stream_id, received_at, priority, is_event)
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 1, 0, @holder, NOW() + INTERVAL '5 minutes'
      FROM m
      """, ("id", id), ("type", eventType), ("sid", Guid.NewGuid()), ("holder", holder));
    return id;
  }

  /// <summary>An ephemeral fan-out event the sink processed, past its grace, at version 5, with the sink's cursor on it.</summary>
  private static async Task<Guid> _consumedEphemeralFanOutAsync(NpgsqlConnection conn) {
    await _executeAsync(conn, "UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'ephemeral_rewind_grace_seconds'");
    var renamed = await _emitOutboxAsync(conn, RENAMED, flags: 8);
    var stream = await _streamOfAsync(conn, renamed);
    await _executeAsync(conn, "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_id = @e", ("e", renamed));
    await _executeAsync(conn,
      "UPDATE wh_event_store SET commit_sequence = 5, version = 5, created_at = NOW() - INTERVAL '10 minutes' WHERE event_id = @e",
      ("e", renamed));
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES (@s, @p, @e, 2)",
      ("s", stream), ("p", SINK), ("e", renamed));
    await Assert.That(await _bodiesAsync(conn, renamed)).IsEqualTo(1L).Because("the body is offloaded at commit");
    return renamed;
  }

  private static async Task _maintainAsync(NpgsqlConnection conn) {
    await using var maintain = new NpgsqlCommand("SELECT * FROM perform_maintenance()", conn);
    await using var reader = await maintain.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain the task rows */ }
  }

  private static Task _emitInboxAsync(NpgsqlConnection conn, Guid instance) =>
    _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)",
      ("inst", instance));

  private static Task<long> _rowsAsync(NpgsqlConnection conn, Guid eventId, string perspective) =>
    _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_events WHERE event_id = @e AND perspective_name = @p",
      ("e", eventId), ("p", perspective));

  private static Task<Guid> _streamOfAsync(NpgsqlConnection conn, Guid eventId) =>
    _scalarAsync<Guid>(conn, "SELECT stream_id FROM wh_event_store WHERE event_id = @e", ("e", eventId));

  private static Task<long> _bodiesAsync(NpgsqlConnection conn, Guid eventId) =>
    _scalarAsync<long>(conn, "SELECT count(*) FROM wh_event_body WHERE event_id = @e", ("e", eventId));

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var scalar = await cmd.ExecuteScalarAsync();
    return scalar is T typed ? typed : (T)Convert.ChangeType(scalar, typeof(T), CultureInfo.InvariantCulture)!;
  }
}
