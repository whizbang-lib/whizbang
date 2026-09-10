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
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 151 (priority step 1 on the wire): the rows the store hands back carry the number the producer stored,
/// and the rows recovery re-creates are declared background. The coalesce fetch returns each single's number so the
/// ship worker can fold it into the composite, the fold stores the composite's number, and a dead letter recovered
/// into any of the three tables re-enters as background work: recovery is repair nobody waits on.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#on-the-wire</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/151_PriorityOnTheWire.sql</code-under-test>
[Category("Shard1")]
public class PriorityOnTheWireSqlTests : EFCoreTestBase {
  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _build(WorkCoordinationDbContext ctx)
    => new(ctx, JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static OutboxMessage _outbox(Guid messageId, Guid streamId, int priority, string? coalesceGroup = null) {
    var envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonDocument.Parse("{\"p\":1}").RootElement, []) { Priority = priority };
    return new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Test.X, Test",
      StreamId = streamId,
      IsEvent = false,
      Priority = priority,
      CoalesceGroup = coalesceGroup,
      ScheduledFor = coalesceGroup is null ? null : DateTimeOffset.UtcNow.AddSeconds(60),
    };
  }

  private static async Task<int> _priorityAsync(NpgsqlConnection conn, string table, string idColumn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT priority FROM {table} WHERE {idColumn} = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<Guid> _moveToDlqAsync(NpgsqlConnection conn, string sourceTable, Guid sourceId) {
    var dlqId = (Guid)TrackedGuid.NewMedo();
    await _execAsync(conn,
      "SELECT move_to_dead_letters(@dlq, @tbl, @src, @reason, @err, @inst, @gen)",
      ("dlq", dlqId), ("tbl", sourceTable), ("src", sourceId), ("reason", 5), ("err", "test"), ("inst", (Guid)TrackedGuid.NewMedo()), ("gen", "v0.502"));
    return dlqId;
  }

  private static async Task<bool> _recoverAsync(NpgsqlConnection conn, Guid dlqId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT recover_dead_letter(@id)";
    cmd.Parameters.AddWithValue("id", dlqId);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  [Test]
  public async Task FetchPendingCoalesce_ReturnsEachSinglesPriorityAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _build(ctx);
    var group = $"digest-{Guid.CreateVersion7():N}";
    var urgent = Guid.CreateVersion7();
    var bulk = Guid.CreateVersion7();
    await coordinator.StoreOutboxMessagesAsync(
      [_outbox(urgent, Guid.CreateVersion7(), WorkPriority.INTERACTIVE, group), _outbox(bulk, Guid.CreateVersion7(), WorkPriority.BACKGROUND, group)],
      partitionCount: 100);

    var singles = await coordinator.FetchPendingCoalesceAsync(group, limit: 10);

    await Assert.That(singles.Single(s => s.MessageId == urgent).Priority).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("the ship worker folds the singles' numbers into the composite; a fetch that drops them folds nothing");
    await Assert.That(singles.Single(s => s.MessageId == bulk).Priority).IsEqualTo(WorkPriority.BACKGROUND);
  }

  [Test]
  public async Task CompleteCoalesceFold_StoresTheCompositesPriorityAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _build(ctx);
    var group = $"digest-{Guid.CreateVersion7():N}";
    var single = Guid.CreateVersion7();
    await coordinator.StoreOutboxMessagesAsync([_outbox(single, Guid.CreateVersion7(), WorkPriority.BACKGROUND, group)], partitionCount: 100);
    var compositeId = Guid.CreateVersion7();

    await coordinator.CompleteCoalesceFoldAsync([single], [_outbox(compositeId, Guid.CreateVersion7(), WorkPriority.BACKGROUND)], partitionCount: 100);

    var conn = await _openAsync(ctx);
    await Assert.That(await _priorityAsync(conn, "wh_outbox", "message_id", compositeId)).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the composite row is what the drain claims and publishes; its number must be the folded one, not the default");
  }

  [Test]
  public async Task RecoverDeadLetter_OutboxRow_ReentersAsBackgroundAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _execAsync(conn, @"
      INSERT INTO wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number, priority)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{}', '{}', 1, 11, NOW(), @stream, 0, 50)",
      ("msg", messageId), ("stream", (Guid)TrackedGuid.NewMedo()));
    var dlqId = await _moveToDlqAsync(conn, "wh_outbox", messageId);

    await Assert.That(await _recoverAsync(conn, dlqId)).IsTrue();

    await Assert.That(await _priorityAsync(conn, "wh_outbox", "message_id", messageId)).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a recovered row is repair work nobody waits on; the number it once had is not the number it re-enters with");
  }

  [Test]
  public async Task RecoverDeadLetter_InboxRow_ReentersAsBackgroundAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _execAsync(conn, @"
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at, stream_id, partition_number, priority)
      VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, 11, NOW(), @stream, 0, 50)",
      ("msg", messageId), ("stream", (Guid)TrackedGuid.NewMedo()));
    var dlqId = await _moveToDlqAsync(conn, "wh_inbox", messageId);

    await Assert.That(await _recoverAsync(conn, dlqId)).IsTrue();

    await Assert.That(await _priorityAsync(conn, "wh_inbox", "message_id", messageId)).IsEqualTo(WorkPriority.BACKGROUND);
  }

  [Test]
  public async Task RecoverDeadLetter_PerspectiveRow_ReentersAsBackgroundAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var workId = (Guid)TrackedGuid.NewMedo();
    await _execAsync(conn, @"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, priority)
      VALUES (@work, @stream, 'TestPerspective', @event, 0, 1, 11, NOW(), 50)",
      ("work", workId), ("stream", (Guid)TrackedGuid.NewMedo()), ("event", (Guid)TrackedGuid.NewMedo()));
    var dlqId = await _moveToDlqAsync(conn, "wh_perspective_events", workId);

    await Assert.That(await _recoverAsync(conn, dlqId)).IsTrue();

    await Assert.That(await _priorityAsync(conn, "wh_perspective_events", "event_work_id", workId)).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a recovered projection catches up behind live projections, the same as a replayed one");
  }

  [Test]
  public async Task RecoverDeadLetter_BrokerRow_ReentersAsBackgroundAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _build(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();
    _ = await coordinator.ImportBrokerDeadLetterAsync(new BrokerDeadLetterImport(
      MessageId: messageId,
      StreamId: (Guid)TrackedGuid.NewMedo(),
      MessageType: "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      Destination: "inbox/test-service-inbox",
      EnvelopeJson: """{"v":1,"p":{"Name":"restored"}}""",
      BrokerReason: "MaxDeliveryAttemptsExceeded",
      BrokerDescription: "test",
      EnqueuedAt: DateTimeOffset.UtcNow.AddDays(-2),
      DeliveryCount: 10));
    var conn = await _openAsync(ctx);
    Guid dlqId;
    await using (var idCmd = conn.CreateCommand()) {
      idCmd.CommandText = "SELECT dead_letter_id FROM wh_dead_letters WHERE source_id = @id AND source_table = 'broker'";
      idCmd.Parameters.AddWithValue("id", messageId);
      dlqId = (Guid)(await idCmd.ExecuteScalarAsync())!;
    }

    await Assert.That(await _recoverAsync(conn, dlqId)).IsTrue();

    await Assert.That(await _priorityAsync(conn, "wh_inbox", "message_id", messageId)).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a message pulled back from the broker's dead-letter queue is catch-up work, whatever number it carried when it failed");
  }
}
