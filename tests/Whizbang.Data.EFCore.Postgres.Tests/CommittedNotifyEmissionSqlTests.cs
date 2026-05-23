using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 26 step 4 — RED-first locks for the <c>wh_committed</c> NOTIFY emission. The
/// commit-order stamper LISTENs on this channel; whenever <c>_emit_event_store_chain</c>
/// (called from <c>store_outbox_messages</c>) or <c>_emit_event_store_chain_for_inbox</c>
/// (called from <c>claim_work</c>) lands new rows in <c>wh_event_store</c>, a single
/// <c>pg_notify('wh_committed', '')</c> must fire so the stamper wakes within ~1ms
/// instead of waiting for the polling tick.
///
/// <para>PG buffers NOTIFY at the transaction level and dedups <c>(channel, payload)</c>
/// at COMMIT. So even when a single transaction stores many events, exactly one
/// <c>wh_committed</c> notification reaches each LISTEN-er.</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public class CommittedNotifyEmissionSqlTests : EFCoreTestBase {

  [Test]
  public async Task EmitEventStoreChain_ViaStoreOutboxMessages_FiresWhCommittedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var received = await _captureNotificationsAsync(conn, "wh_committed", async () => {
      // Stamper subscribes BEFORE the producer fires; without this LISTEN the NOTIFY
      // is delivered but the test connection sees nothing. Production timing is the
      // same — stamper has continuous LISTEN.
      var messagesJson = $$"""
        [{
          "MessageId": "{{msgId}}",
          "Destination": "topic",
          "MessageType": "TestEvent",
          "EnvelopeType": "MessageEnvelope",
          "Envelope": {"p": {}, "Hops": []},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": true
        }]
        """;
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT * FROM store_outbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      cmd.Parameters.AddWithValue("p", messagesJson);
      cmd.Parameters.AddWithValue("inst", instanceId);
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) { /* drain */ }
    });

    await Assert.That(received).Count().IsGreaterThanOrEqualTo(1)
      .Because("storing an event with stream_id triggers _emit_event_store_chain which must fire NOTIFY wh_committed");
  }

  [Test]
  public async Task EmitEventStoreChain_ManyEventsInOneTx_DedupesToOneNotifyAsync() {
    // PG buffers NOTIFY and dedups (channel, payload) within the same transaction at
    // COMMIT. Many events stored in one tx → exactly one wh_committed notification.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    var received = await _captureNotificationsAsync(conn, "wh_committed", async () => {
      // Wrap multiple stores in an explicit tx so dedup is observable.
      await using (var begin = conn.CreateCommand()) {
        begin.CommandText = "BEGIN";
        await begin.ExecuteNonQueryAsync();
      }

      for (var i = 1; i <= 5; i++) {
        var msgId = (Guid)TrackedGuid.NewMedo();
        var messagesJson = $$"""
          [{
            "MessageId": "{{msgId}}",
            "Destination": "topic",
            "MessageType": "TestEvent",
            "EnvelopeType": "MessageEnvelope",
            "Envelope": {"p": {}, "Hops": []},
            "Metadata": {},
            "Scope": null,
            "StreamId": "{{streamId}}",
            "IsEvent": true
          }]
          """;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM store_outbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
        cmd.Parameters.AddWithValue("p", messagesJson);
        cmd.Parameters.AddWithValue("inst", instanceId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { /* drain */ }
      }

      await using (var commit = conn.CreateCommand()) {
        commit.CommandText = "COMMIT";
        await commit.ExecuteNonQueryAsync();
      }
    });

    await Assert.That(received).Count().IsEqualTo(1)
      .Because("PG dedups (channel, payload) within a tx — 5 stores → 1 delivered NOTIFY");
  }

  [Test]
  public async Task EmitEventStoreChain_NoEventsStored_DoesNotFireWhCommittedAsync() {
    // A non-event outbox row (IsEvent=false) doesn't enter wh_event_store. No NOTIFY
    // should fire on wh_committed — the stamper has no work for non-event-store inserts.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var received = await _captureNotificationsAsync(conn, "wh_committed", async () => {
      var messagesJson = $$"""
        [{
          "MessageId": "{{msgId}}",
          "Destination": "topic",
          "MessageType": "TestCommand",
          "EnvelopeType": "MessageEnvelope",
          "Envelope": {"p": {}, "Hops": []},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": false
        }]
        """;
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT * FROM store_outbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      cmd.Parameters.AddWithValue("p", messagesJson);
      cmd.Parameters.AddWithValue("inst", instanceId);
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) { /* drain */ }
    });

    await Assert.That(received).Count().IsEqualTo(0)
      .Because("non-event outbox messages don't land in wh_event_store; no NOTIFY needed");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  /// <summary>
  /// LISTENs on <paramref name="channel"/>, runs <paramref name="emit"/>, forces a
  /// network roundtrip to flush pending notifications, returns the received list.
  /// </summary>
  private static async Task<List<string>> _captureNotificationsAsync(
      NpgsqlConnection conn, string channel, Func<Task> emit) {
    var received = new List<string>();
    NotificationEventHandler handler = (sender, args) => {
      if (string.Equals(args.Channel, channel, StringComparison.Ordinal)) {
        received.Add(args.Payload);
      }
    };
    conn.Notification += handler;
    try {
      await using (var listen = conn.CreateCommand()) {
        listen.CommandText = $"LISTEN {channel}";
        await listen.ExecuteNonQueryAsync();
      }

      await emit();

      // Force a roundtrip so any pending NOTIFYs dispatch to the handler before we read.
      await using var ping = conn.CreateCommand();
      ping.CommandText = "SELECT 1";
      _ = await ping.ExecuteScalarAsync();
    } finally {
      conn.Notification -= handler;
      await using var unlisten = conn.CreateCommand();
      unlisten.CommandText = $"UNLISTEN {channel}";
      await unlisten.ExecuteNonQueryAsync();
    }
    return received;
  }
}
