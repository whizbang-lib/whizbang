using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the separation of the doorbell-debounce KEY from the NOTIFY PAYLOAD (issue #702).
/// <para>Migration 130 keyed <c>wh_notify_state</c> on <c>(instance_id, payload_kind)</c> with the
/// kind sized to the closed doorbell vocabulary (<c>outbox</c>, <c>inbox</c>, <c>perspective</c>,
/// <c>schedule</c>), and 137 made the fire path insert the value unconditionally. But
/// <c>notify_instance_owners(p_payload, p_stream_ids)</c> had a second caller: the signal transport
/// passes a signal's wire name, whose default is the fully qualified type name. One parameter carried
/// a closed enum from SQL and an open-ended type name from C#, and a targeted signal with a wire
/// name over twenty characters failed to publish (22001).</para>
/// <para>Fix: <c>_notify_debounced(instance, kind, payload, window)</c> and
/// <c>notify_instance_owners_with_payload(kind, payload, stream_ids)</c> take the two concepts separately; the
/// key column is unbounded text; the two-argument form is the DOORBELL form, whose payload is its
/// kind by definition and which accepts only the doorbell vocabulary.</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
[Category("Shard1")]
public class NotifyKindAndPayloadSqlTests : EFCoreTestBase {

  [Test]
  public async Task NotifyState_KeyColumnIsUnboundedTextAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT data_type FROM information_schema.columns
                        WHERE table_name = 'wh_notify_state' AND column_name = 'payload_kind'
                          AND table_schema = current_schema()";
    var dataType = (string?)await cmd.ExecuteScalarAsync();

    await Assert.That(dataType).IsEqualTo("text")
      .Because("a signal's wire name defaults to its fully qualified type name; the key must not be sized to the doorbell vocabulary");
  }

  [Test]
  public async Task NotifyInstanceOwners_KindAndPayloadAreIndependent_PayloadReachesTheWire_KindKeysTheStateAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var owner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _ownStreamAsync(conn, streamId, owner);
    const string kind = "utest-kind-longer-than-twenty-characters";
    const string payload = "utest-payload-longer-than-twenty-characters";

    var received = await _captureNotificationsAsync(conn, [owner], async () =>
      await _notifyAsync(conn, kind, payload, streamId));

    await Assert.That(received).Count().IsEqualTo(1);
    await Assert.That(received[0].Channel).IsEqualTo($"wh_work_i_{owner}");
    await Assert.That(received[0].Payload).IsEqualTo(payload)
      .Because("the wire carries the payload, never the debounce key");
    await Assert.That(await _stateRowExistsAsync(conn, owner, kind)).IsTrue()
      .Because("the debounce state is keyed by the kind");
    await Assert.That(await _stateRowExistsAsync(conn, owner, payload)).IsFalse()
      .Because("the payload is not a key");
  }

  [Test]
  public async Task NotifyInstanceOwners_SignalWireNameLongerThanTwentyCharacters_FiresAsync() {
    // The exact #702 symptom: a signal keyed and carried by its default wire name, the fully
    // qualified type name.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var owner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _ownStreamAsync(conn, streamId, owner);
    const string wireName = "ConsumerService.Signals.ProjectionCoverageGapDetected";

    var received = await _captureNotificationsAsync(conn, [owner], async () =>
      await _notifyAsync(conn, wireName, wireName, streamId));

    await Assert.That(received).Count().IsEqualTo(1);
    await Assert.That(received[0].Payload).IsEqualTo(wireName);
    await Assert.That(await _stateRowExistsAsync(conn, owner, wireName)).IsTrue();
  }

  [Test]
  public async Task NotifyInstanceOwners_DoorbellForm_AcceptsOnlyTheDoorbellVocabularyAsync() {
    // The two-argument form is the doorbell form: its payload IS its kind. An open-ended value
    // is the drift that produced #702, so it is rejected up front with a hint toward the
    // three-argument form, instead of failing later on the key column.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var streamId = (Guid)TrackedGuid.NewMedo();

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT notify_instance_owners(@payload, ARRAY[@sid]::uuid[])";
    cmd.Parameters.AddWithValue("payload", "ConsumerService.Signals.ProjectionCoverageGapDetected");
    cmd.Parameters.AddWithValue("sid", streamId);
    var ex = await Assert.ThrowsAsync<PostgresException>(async () => await cmd.ExecuteNonQueryAsync());

    await Assert.That(ex!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidParameterValue);
    await Assert.That(ex.Message).Contains("notify_instance_owners_with_payload(kind, payload, stream_ids)");
  }

  [Test]
  public async Task NotifyInstanceOwners_DoorbellForm_PayloadIsTheKindAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var owner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _ownStreamAsync(conn, streamId, owner);

    var received = await _captureNotificationsAsync(conn, [owner], async () => {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT notify_instance_owners('schedule', ARRAY[@sid]::uuid[])";
      cmd.Parameters.AddWithValue("sid", streamId);
      await cmd.ExecuteNonQueryAsync();
    });

    await Assert.That(received).Count().IsEqualTo(1);
    await Assert.That(received[0].Payload).IsEqualTo("schedule");
    await Assert.That(await _stateRowExistsAsync(conn, owner, "schedule")).IsTrue();
  }

  // -------------------------------------------------------------------------------------------

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _ownStreamAsync(NpgsqlConnection conn, Guid streamId, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at)
                        VALUES (@sid, 0, @inst, NOW())";
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _notifyAsync(NpgsqlConnection conn, string kind, string payload, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT notify_instance_owners_with_payload(@kind, @payload, ARRAY[@sid]::uuid[])";
    cmd.Parameters.AddWithValue("kind", kind);
    cmd.Parameters.AddWithValue("payload", payload);
    cmd.Parameters.AddWithValue("sid", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<bool> _stateRowExistsAsync(NpgsqlConnection conn, Guid instanceId, string kind) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM wh_notify_state WHERE instance_id = @id AND payload_kind = @kind)";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("kind", kind);
    return (bool)(await cmd.ExecuteScalarAsync() ?? false);
  }

  private static async Task<List<(string Channel, string Payload)>> _captureNotificationsAsync(
      NpgsqlConnection conn, Guid[] ownersToListen, Func<Task> emit) {
    var received = new List<(string Channel, string Payload)>();
    void handler(object _, NpgsqlNotificationEventArgs e) => received.Add((e.Channel, e.Payload));
    conn.Notification += handler;
    try {
      foreach (var owner in ownersToListen) {
        await using var listen = conn.CreateCommand();
        listen.CommandText = $"LISTEN \"wh_work_i_{owner}\"";
        await listen.ExecuteNonQueryAsync();
      }

      await emit();
      // 146 (#720): the functions under test queue their doorbells instead of notifying inside the
      // transaction; the caller rings after the commit. This models the driver's DoorbellRinger.
      await using (var ring = conn.CreateCommand()) {
        ring.CommandText = "SELECT ring_doorbells()";
        _ = await ring.ExecuteScalarAsync();
      }

      // Force a roundtrip: NOTIFY messages buffered after the function's COMMIT are dispatched
      // to the Notification event on the next request/response cycle.
      await using var ping = conn.CreateCommand();
      ping.CommandText = "SELECT 1";
      _ = await ping.ExecuteScalarAsync();
    } finally {
      conn.Notification -= handler;
      foreach (var owner in ownersToListen) {
        await using var unlisten = conn.CreateCommand();
        unlisten.CommandText = $"UNLISTEN \"wh_work_i_{owner}\"";
        await unlisten.ExecuteNonQueryAsync();
      }
    }
    return received;
  }
}
