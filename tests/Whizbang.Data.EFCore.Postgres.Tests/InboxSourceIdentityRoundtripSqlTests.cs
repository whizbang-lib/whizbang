using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 26.6 — RED-first locks for the inbox source-identity round-trip. The transport
/// consumer extracts <c>SourceServiceId</c> / <c>SourceCommitSequence</c> from the inbound
/// envelope and includes them in the JSONB payload to <c>store_inbox_messages</c>; the
/// SQL function persists them into the <c>wh_inbox.source_service_id</c> /
/// <c>source_commit_sequence</c> columns.
///
/// <para>For tests that don't go through the transport consumer, we call
/// <c>store_inbox_messages</c> directly with the new envelope fields in the JSONB array.
/// Locks the SQL contract: explicit values in the payload override the DB-default
/// (local service identity + 0).</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
[Category("Shard1")]
public class InboxSourceIdentityRoundtripSqlTests : EFCoreTestBase {

  [Test]
  public async Task StoreInboxMessages_ExplicitSourceIdentity_PersistsToColumnsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var sourceServiceId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var localInstanceId = (Guid)TrackedGuid.NewMedo();

    var messagesJson = $$"""
      [{
        "MessageId": "{{msgId}}",
        "HandlerName": "TestHandler",
        "MessageType": "TestEvent",
        "EnvelopeType": "MessageEnvelope",
        "Envelope": {"p": {}, "h": []},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": false,
        "SourceServiceId": "{{sourceServiceId}}",
        "SourceCommitSequence": 42
      }]
      """;

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT * FROM store_inbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      cmd.Parameters.AddWithValue("p", messagesJson);
      cmd.Parameters.AddWithValue("inst", localInstanceId);
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) { /* drain */ }
    }

    var (storedSourceServiceId, storedCommitSequence) = await _readInboxSourceIdentityAsync(conn, msgId);
    await Assert.That(storedSourceServiceId).IsEqualTo(sourceServiceId)
      .Because("explicit SourceServiceId in the payload must override the DB-default");
    await Assert.That(storedCommitSequence).IsEqualTo(42L);
  }

  [Test]
  public async Task StoreInboxMessages_OmittedSourceIdentity_UsesLocalDefaultAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var localInstanceId = (Guid)TrackedGuid.NewMedo();

    var messagesJson = $$"""
      [{
        "MessageId": "{{msgId}}",
        "HandlerName": "TestHandler",
        "MessageType": "TestEvent",
        "EnvelopeType": "MessageEnvelope",
        "Envelope": {"p": {}, "h": []},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": false
      }]
      """;

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT * FROM store_inbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      cmd.Parameters.AddWithValue("p", messagesJson);
      cmd.Parameters.AddWithValue("inst", localInstanceId);
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) { /* drain */ }
    }

    // Defaults: source_service_id = wh_service_config.service_id, source_commit_sequence = 0.
    var (storedSourceServiceId, storedCommitSequence) = await _readInboxSourceIdentityAsync(conn, msgId);
    var localServiceId = await _readLocalServiceIdAsync(conn);
    await Assert.That(storedSourceServiceId).IsEqualTo(localServiceId)
      .Because("when payload omits SourceServiceId, store_inbox_messages COALESCEs to local wh_service_config.service_id");
    await Assert.That(storedCommitSequence).IsEqualTo(0L);
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

  private static async Task<(Guid SourceServiceId, long SourceCommitSequence)> _readInboxSourceIdentityAsync(
      NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT source_service_id, source_commit_sequence FROM wh_inbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"No wh_inbox row for {messageId}");
    }
    return (reader.GetGuid(0), reader.GetInt64(1));
  }

  private static async Task<Guid> _readLocalServiceIdAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT service_id FROM wh_service_config LIMIT 1";
    var result = await cmd.ExecuteScalarAsync();
    return (Guid)result!;
  }
}
