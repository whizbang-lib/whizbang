// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2350: a consumer that reaped the older copy of a superseding key before its origin did reads as short, and a
/// redelivery restores that copy. The restored copy is stored at the end of the consumer's stream, above the newest copy
/// by local version, so ordering the copies of a key by local version made the stale copy the newest: the reaper would
/// remove the real newest copy and keep the stale one. Copies received from the same origin are ordered by the origin's
/// commit sequence instead, the order the origin wrote them in. A redelivered copy older than one already held is never
/// stored: it is settled as satisfied, so no perspective applies it after the newest and the newest is never reaped.
/// </summary>
/// <docs>fundamentals/events/superseding-events#redelivered-copies</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/449_RedeliveredCopiesKeepTheOriginsOrder.sql</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class RedeliveredSupersedingCopySqlTests : EFCoreTestBase {
  private const string TYPE = "Probe.Contracts.ProfileReplaced, Probe.Contracts";
  private static readonly Guid _origin = Guid.Parse("0000000a-0000-0000-0000-000000000001");
  private static readonly string[] _originNames = ["origin-svc"];
  private static readonly int[] _oneBundle = [1];
  private static readonly long[] _olderSequence = [10];

  [Test]
  public async Task ARedeliveredOlderCopy_IsNeverStored_IsRecordedSatisfied_AndNoPerspectiveSeesItAsync() {
    await using var dbContext = CreateDbContext();
    var stream = TrackedGuid.New().Value;
    var first = TrackedGuid.New().Value;
    var older = TrackedGuid.New().Value;
    var newest = TrackedGuid.New().Value;
    // Live delivery: a key written once, then both copies of key k, in the origin's order. The superseded copy is not
    // the stream's first event, so the restore is not refused as history below a truncation.
    var conn = await _openAsync(dbContext);
    await _executeAsync(conn, """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @t, 'perspective', 'ProfilePerspective', 'test-service', @t, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """, ("t", TYPE));
    _ = await _receiveAsync(dbContext, _received(stream, first, "once", originSequence: 5),
      _received(stream, older, "k", originSequence: 10), _received(stream, newest, "k", originSequence: 20));
    await Assert.That(await _countAsync(conn, "SELECT COUNT(*) FROM wh_perspective_events WHERE event_id = @id", newest)).IsEqualTo(1L)
      .Because("precondition: the perspective is offered the live copies, so an offer of the restored one would show");
    // Handled: the perspective applied them, the inbox let go of them, and deduplication remembers them.
    await _executeAsync(conn, "UPDATE wh_perspective_events SET processed_at = NOW() WHERE stream_id = @s", ("s", stream));
    await _executeAsync(conn, "DELETE FROM wh_inbox_state WHERE message_id = ANY(@ids); DELETE FROM wh_inbox WHERE message_id = ANY(@ids)",
      ("ids", new[] { first, older, newest }));
    await _stampAsync(conn);
    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L).Because("precondition: the consumer reaps the older copy first");
    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([first, newest]);

    // The origin still holds the older copy, so the comparison reads a deficit and a redelivery carries it again.
    _ = await _receiveAsync(dbContext, _received(stream, older, "k", originSequence: 10, restore: true));

    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([first, newest])
      .Because("a newer copy of its key is held, so the older copy is never stored again");
    await Assert.That(await _errorAsync(conn, older)).IsEqualTo(_satisfied())
      .Because("it is settled with the reason, never claimed, copied or applied");
    await Assert.That(await _countAsync(conn,
        "SELECT COUNT(*) FROM wh_perspective_events WHERE event_id = @id AND processed_at IS NULL", older)).IsEqualTo(0L)
      .Because("no perspective is offered the older copy after the newer one (its live offer was processed before the reap)");
    var answer = await _reconcileAsync(conn, older);
    await Assert.That(answer).IsEquivalentTo([$"{stream}|1|0|1"])
      .Because("the reconcile reads the copy as satisfied (present) and counts it apart, never as a deficit");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AnOlderCopyArrivingForTheFirstTime_AfterTheNewest_IsNeverStored_AndIsRecordedSatisfiedAsync(bool byRedelivery) {
    await using var dbContext = CreateDbContext();
    var stream = TrackedGuid.New().Value;
    var first = TrackedGuid.New().Value;
    var older = TrackedGuid.New().Value;
    var newest = TrackedGuid.New().Value;
    var conn = await _openAsync(dbContext);
    await _executeAsync(conn, """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @t, 'perspective', 'ProfilePerspective', 'test-service', @t, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """, ("t", TYPE));
    // The newest copy of key k arrives; the older one never has: it was lost in transit, or arrives out of order.
    _ = await _receiveAsync(dbContext, _received(stream, first, "once", originSequence: 5),
      _received(stream, newest, "k", originSequence: 20));

    // Its first arrival: a redelivery (an integrity repair or an operator's) carries it, or the live receive does.
    _ = await _receiveAsync(dbContext, _received(stream, older, "k", originSequence: 10, restore: byRedelivery));

    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([first, newest])
      .Because("a newer copy of its key is held, so the older copy is never stored, whichever path brings it");
    await Assert.That(await _errorAsync(conn, older)).IsEqualTo(_satisfied())
      .Because("it is settled with the reason, never claimed, copied or applied");
    await Assert.That(await _countAsync(conn, "SELECT COUNT(*) FROM wh_perspective_events WHERE event_id = @id", older))
      .IsEqualTo(0L).Because("a copy never stored is never offered to a perspective");
    await Assert.That(await _reconcileAsync(conn, older)).IsEquivalentTo([$"{stream}|1|0|1"])
      .Because("the reconcile reads the copy as satisfied (present) and counts it apart, never as a deficit");
  }

  [Test]
  public async Task ARedeliveredCopyNewerThanTheHeldOne_IsRestoredAsBeforeAsync() {
    await using var dbContext = CreateDbContext();
    var stream = TrackedGuid.New().Value;
    var first = TrackedGuid.New().Value;
    var older = TrackedGuid.New().Value;
    var lost = TrackedGuid.New().Value;
    var conn = await _receiveAsync(dbContext, _received(stream, first, "once", originSequence: 5),
      _received(stream, older, "k", originSequence: 10), _received(stream, lost, "k", originSequence: 20));
    await _executeAsync(conn, "DELETE FROM wh_inbox_state WHERE message_id = ANY(@ids); DELETE FROM wh_inbox WHERE message_id = ANY(@ids)",
      ("ids", new[] { first, older, lost }));
    await _executeAsync(conn, "DELETE FROM wh_superseded_events; DELETE FROM wh_event_store WHERE event_id = @id", ("id", lost));

    _ = await _receiveAsync(dbContext, _received(stream, lost, "k", originSequence: 20, restore: true));

    await Assert.That(await _storedAsync(conn, stream)).Contains(lost)
      .Because("the held copy is older, so the lost newest copy is restored");
  }

  [Test]
  public async Task CopiesWithoutTheOriginsSequence_AreStillOrderedByVersion_AsBeforeAsync() {
    await using var dbContext = CreateDbContext();
    var stream = TrackedGuid.New().Value;
    var first = TrackedGuid.New().Value;
    var second = TrackedGuid.New().Value;

    var conn = await _receiveAsync(dbContext, _received(stream, first, "k", originSequence: null),
      _received(stream, second, "k", originSequence: 5));

    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([first])
      .Because("a copy with no origin sequence cannot be placed in the origin's order, so the stored order decides");
  }

  // ── helpers ──

  private static InboxMessage _received(Guid streamId, Guid messageId, string key, long? originSequence, bool restore = false) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.SerializeToElement(new { key }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      SourceServiceId = _origin,
      SourceCommitSequence = originSequence ?? 0,
    };
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "ProbeHandler",
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = TYPE,
      StreamId = streamId,
      IsEvent = true,
      Flags = EventFlags.Superseding,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [], SupersedingKey = key },
      SourceServiceId = _origin,
      SourceCommitSequence = originSequence ?? 0,
      RestoreIfMissing = restore,
    };
  }

  // Stores the rows in the inbox, leases them to one instance and runs the claim's emit chain over them.
  private static async Task<NpgsqlConnection> _receiveAsync(WorkCoordinationDbContext dbContext, params InboxMessage[] rows) {
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.StoreInboxMessagesAsync(rows, partitionCount: 100);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var instanceId = TrackedGuid.New().Value;
    Guid[] ids = [.. rows.Select(r => r.MessageId)];
    await _executeAsync(conn,
      "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
      ("inst", instanceId), ("ids", ids));
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)",
      ("inst", instanceId));
    return conn;
  }

  private static string _satisfied() =>
    Whizbang.Data.Postgres.MigrationConstants.Tokens["__REPAIR_SATISFIED_SUPERSEDED__"].Trim('\'');

  private static async Task<string?> _errorAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT error FROM wh_inbox_state WHERE message_id = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull ? null : (string)value;
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn, string sql, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue(nameof(id), id);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>The bundle reconcile for one child: stream | present | adopted | superseded-satisfied.</summary>
  private static async Task<List<string>> _reconcileAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stream_id, present_count, adopted_count, superseded_satisfied_count "
      + "FROM wh_reconcile_redelivered_bundles_counting_satisfied(@origins, @names, @bundles, @ids, @seqs)";
    cmd.Parameters.AddWithValue("origins", new[] { _origin });
    cmd.Parameters.AddWithValue("names", _originNames);
    cmd.Parameters.AddWithValue("bundles", _oneBundle);
    cmd.Parameters.AddWithValue("ids", new[] { id });
    cmd.Parameters.AddWithValue("seqs", _olderSequence);
    var rows = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add($"{reader.GetGuid(0)}|{reader.GetInt32(1)}|{reader.GetInt32(2)}|{reader.GetInt32(3)}");
    }
    return rows;
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static Task _stampAsync(NpgsqlConnection conn) => _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");

  private static async Task<long> _reapAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT rows_affected FROM wh_reap_superseded_events(5000)";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }


  private static Task<List<Guid>> _queuedAsync(NpgsqlConnection conn) =>
    _idsAsync(conn, "SELECT event_id FROM wh_superseded_events ORDER BY queued_at, event_id");

  private static Task<List<Guid>> _storedAsync(NpgsqlConnection conn, Guid stream) =>
    _idsAsync(conn, "SELECT event_id FROM wh_event_store WHERE stream_id = @s ORDER BY version", ("s", stream));

  private static async Task<List<Guid>> _idsAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var rows = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetGuid(0));
    }
    return rows;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
