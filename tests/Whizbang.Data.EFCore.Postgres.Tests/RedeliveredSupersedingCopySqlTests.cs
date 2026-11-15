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
/// commit sequence instead, the order the origin wrote them in, so a restored older copy is recognized as superseded on
/// arrival and the newest copy is never queued or reaped.
/// </summary>
/// <docs>fundamentals/events/superseding-events#redelivered-copies</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/449_RedeliveredCopiesKeepTheOriginsOrder.sql</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class RedeliveredSupersedingCopySqlTests : EFCoreTestBase {
  private const string TYPE = "Probe.Contracts.ProfileReplaced, Probe.Contracts";
  private static readonly Guid _origin = Guid.Parse("0000000a-0000-0000-0000-000000000001");

  [Test]
  public async Task ARestoredOlderCopy_IsQueuedOnArrival_AndTheNewestCopyIsNeverQueuedOrReapedAsync() {
    await using var dbContext = CreateDbContext();
    var stream = TrackedGuid.New().Value;
    var first = TrackedGuid.New().Value;
    var older = TrackedGuid.New().Value;
    var newest = TrackedGuid.New().Value;
    // Live delivery: a key written once, then both copies of key k, in the origin's order. The superseded copy is not
    // the stream's first event, so restoring it is not refused as history below a truncation.
    var conn = await _receiveAsync(dbContext, _received(stream, first, "once", originSequence: 5),
      _received(stream, older, "k", originSequence: 10), _received(stream, newest, "k", originSequence: 20));
    // Handled: the inbox lets go of them, and deduplication remembers them.
    await _executeAsync(conn, "DELETE FROM wh_inbox_state WHERE message_id = ANY(@ids); DELETE FROM wh_inbox WHERE message_id = ANY(@ids)",
      ("ids", new[] { first, older, newest }));
    await _stampAsync(conn);
    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L).Because("precondition: the consumer reaps the older copy first");
    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([first, newest]);

    // The origin still holds the older copy, so the comparison reads a deficit and a redelivery restores it.
    _ = await _receiveAsync(dbContext, _received(stream, older, "k", originSequence: 10, restore: true));

    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([first, newest, older])
      .Because("precondition: the restored copy is stored again, above the newest by local version");
    await Assert.That(await _keyAsync(conn, older)).IsEqualTo("k");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([older])
      .Because("the restored copy is older in the origin's order, so it is the one superseded, never the newest");

    await _stampAsync(conn);
    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L);
    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([first, newest])
      .Because("the reaper's own check orders the copies the same way, so the newest copy survives");
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

  private static Task _stampAsync(NpgsqlConnection conn) => _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");

  private static async Task<long> _reapAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT rows_affected FROM wh_reap_superseded_events(5000)";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<string?> _keyAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT supersede_key FROM wh_event_store WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    var value = await cmd.ExecuteScalarAsync();
    return value is DBNull ? null : (string?)value;
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
