using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 149 (priority step 1): every work row carries an effective priority. The store functions read
/// the message's <c>Priority</c> and write it to the row's <c>priority</c> column, an undeclared (zero)
/// number lands in the standard band, the inbox fetch returns it so the dispatch worker can enter it as the
/// ambient parent, and the perspective work created from an inbox event inherits the event row's number.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#storage</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/149_MessagePriority.sql</code-under-test>
[Category("Shard1")]
public class MessagePrioritySqlTests : EFCoreTestBase {

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _build(WorkCoordinationDbContext ctx)
    => new(ctx, JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static InboxMessage _inbox(Guid messageId, Guid streamId, int priority, string messageType = "Test.X, Test") {
    var envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonDocument.Parse("{\"p\":1}").RootElement, []);
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = envelope,
      EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{messageType}]], Whizbang.Core",
      MessageType = messageType,
      StreamId = streamId,
      IsEvent = true,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
      Priority = priority,
    };
  }

  private static OutboxMessage _outbox(Guid messageId, Guid streamId, int priority) {
    var envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonDocument.Parse("{\"p\":1}").RootElement, []);
    return new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Test.X, Test",
      StreamId = streamId,
      IsEvent = true,
      Priority = priority,
    };
  }

  private static async Task<int> _priorityAsync(NpgsqlConnection conn, string table, string idColumn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT priority FROM {table} WHERE {idColumn} = @id";
    cmd.Parameters.AddWithValue("id", id);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _leaseInboxAsync(NpgsqlConnection conn, Guid messageId, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_inbox SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @id";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("id", messageId);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task StoreInboxMessages_WritesTheEffectivePriority_AndReadsUndeclaredAsStandardAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _build(ctx);
    var conn = await _openAsync(ctx);
    var declared = Guid.CreateVersion7();
    var undeclared = Guid.CreateVersion7();

    await coordinator.StoreInboxMessagesAsync([_inbox(declared, Guid.CreateVersion7(), 42), _inbox(undeclared, Guid.CreateVersion7(), WorkPriority.UNDECLARED)], partitionCount: 100);

    await Assert.That(await _priorityAsync(conn, "wh_inbox", "message_id", declared)).IsEqualTo(42)
      .Because("the effective number the consumer classified is what the claim will order by");
    await Assert.That(await _priorityAsync(conn, "wh_inbox", "message_id", undeclared)).IsEqualTo(WorkPriority.STANDARD)
      .Because("a row is never stored with zero; a caller that predates the column lands in the standard band");
  }

  [Test]
  public async Task StoreOutboxMessages_WritesTheDeclaredPriority_AndReadsUndeclaredAsStandardAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _build(ctx);
    var conn = await _openAsync(ctx);
    var declared = Guid.CreateVersion7();
    var undeclared = Guid.CreateVersion7();

    await coordinator.StoreOutboxMessagesAsync([_outbox(declared, Guid.CreateVersion7(), 30), _outbox(undeclared, Guid.CreateVersion7(), WorkPriority.UNDECLARED)], partitionCount: 100);

    await Assert.That(await _priorityAsync(conn, "wh_outbox", "message_id", declared)).IsEqualTo(30);
    await Assert.That(await _priorityAsync(conn, "wh_outbox", "message_id", undeclared)).IsEqualTo(WorkPriority.STANDARD);
  }

  [Test]
  public async Task FetchInboxBatch_ReturnsTheRowsPriorityAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _build(ctx);
    var conn = await _openAsync(ctx);
    var messageId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var instance = Guid.CreateVersion7();
    await coordinator.StoreInboxMessagesAsync([_inbox(messageId, streamId, 42)], partitionCount: 100);
    await _leaseInboxAsync(conn, messageId, instance);

    var rows = await coordinator.FetchInboxBatchAsync([streamId], instance, maxPerStream: 10);

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].Priority).IsEqualTo(42)
      .Because("the dispatch worker enters the row's number as the ambient parent, so the fetch must carry it");
  }

  [Test]
  public async Task ClaimWork_PerspectiveWorkCreatedFromAClaimedInboxEvent_InheritsTheEventRowsPriorityAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _build(ctx);
    var conn = await _openAsync(ctx);
    const string eventType = "Whizbang.Tests.PriorityCarriedEvent";
    var messageId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var instance = Guid.CreateVersion7();

    await using (var assoc = conn.CreateCommand()) {
      assoc.CommandText = @"
        INSERT INTO wh_message_associations
          (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
        VALUES (gen_random_uuid(), @eventType, 'perspective', 'PriorityTestPerspective', 'test-service', @eventType, NOW(), NOW())
        ON CONFLICT DO NOTHING";
      assoc.Parameters.AddWithValue("eventType", eventType);
      await assoc.ExecuteNonQueryAsync();
    }
    await coordinator.StoreInboxMessagesAsync([_inbox(messageId, streamId, 42, eventType)], partitionCount: 100);
    await _registerInstanceAsync(conn, instance);

    // The claim leases the row and copies the leased event into the event store, which is where the
    // perspective work for it is created.
    await using (var claim = conn.CreateCommand()) {
      claim.CommandText = "SELECT count(*) FROM claim_work(@inst, 'test', 'test-host', 1, 10, 100, 300, 0.5, 10, FALSE, NULL)";
      claim.Parameters.AddWithValue("inst", instance);
      _ = await claim.ExecuteScalarAsync();
    }

    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT priority FROM wh_perspective_events WHERE event_id = @eid AND perspective_name = 'PriorityTestPerspective'";
    verify.Parameters.AddWithValue("eid", messageId);
    var priority = await verify.ExecuteScalarAsync();
    await Assert.That(priority).IsNotNull().Because("the claim stores the leased event and creates the perspective work row for the registered association");
    await Assert.That((int)priority!).IsEqualTo(42)
      .Because("the perspective work for an event is scheduled with the event's number, so an interactive event's projection is not queued as standard");
  }
}
