using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Broker DLQ import (proposal: broker-dlq-import; issue #514). A broker-dead-lettered message
/// gets durable custody as a wh_dead_letters row — source_table='broker', failure_reason=17
/// (BrokerDeadLetter), RAW body stored verbatim, broker reason preserved — idempotent on the wire
/// message id; recovery re-emits broker rows into wh_inbox, the same front door every received
/// message uses.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/118_BrokerDeadLetterImport.sql</code-under-test>
[Category("Shard3")]
public class BrokerDeadLetterImportSqlTests : EFCoreTestBase {

  private static Whizbang.Core.Messaging.IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static BrokerDeadLetterImport _import(Guid messageId, Guid? streamId = null, string body = """{"v":1,"p":{"Name":"restored"}}""") =>
    new(
      MessageId: messageId,
      StreamId: streamId,
      MessageType: "Whizbang.Core.Observability.MessageEnvelope`1[[Test.Composite, Test]], Whizbang.Core",
      Destination: "inbox/test-service-inbox",
      EnvelopeJson: body,
      BrokerReason: "MaxDeliveryAttemptsExceeded",
      BrokerDescription: "JsonTypeInfo metadata for type X was not provided",
      EnqueuedAt: DateTimeOffset.UtcNow.AddDays(-2),
      DeliveryCount: 10);

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  [Test]
  public async Task Import_CreatesCustodyRow_WithBrokerProvenanceAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    var imported = await coordinator.ImportBrokerDeadLetterAsync(_import(messageId, streamId));

    await Assert.That(imported).IsTrue()
      .Because("issue #514: a broker-dead-lettered message must transfer custody into "
             + "wh_dead_letters — the default no-op coordinator loses it forever");

    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT source_table, failure_reason, error_text, stream_id, " +
      "envelope -> 'event_data' ->> 'v', metadata ->> 'broker_reason' " +
      "FROM wh_dead_letters WHERE source_id = @id AND source_table = 'broker'";
    cmd.Parameters.AddWithValue("id", messageId);
    await using var reader = await cmd.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    await Assert.That(reader.GetString(0)).IsEqualTo("broker");
    await Assert.That(reader.GetInt32(1)).IsEqualTo(17)
      .Because("MessageFailureReason.BrokerDeadLetter");
    await Assert.That(reader.GetString(2)).Contains("MaxDeliveryAttemptsExceeded")
      .Because("the broker's reason must be preserved, not discarded");
    await Assert.That(reader.GetGuid(3)).IsEqualTo(streamId);
    await Assert.That(reader.GetString(4)).IsEqualTo("1")
      .Because("the wire body is stored verbatim under envelope.event_data");
    await Assert.That(reader.GetString(5)).IsEqualTo("MaxDeliveryAttemptsExceeded");
  }

  [Test]
  public async Task Import_SameMessageTwice_SecondReturnsFalse_OneRowAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();

    var first = await coordinator.ImportBrokerDeadLetterAsync(_import(messageId));
    var second = await coordinator.ImportBrokerDeadLetterAsync(_import(messageId));

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse()
      .Because("import is idempotent on the wire message id — a drain pass that crashes "
             + "mid-batch and re-receives must not double-import");

    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_dead_letters WHERE source_id = @id AND source_table = 'broker'";
    cmd.Parameters.AddWithValue("id", messageId);
    await Assert.That((long)(await cmd.ExecuteScalarAsync())!).IsEqualTo(1L);
  }

  [Test]
  public async Task Import_NonJsonBody_StillGetsCustodyAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();

    var imported = await coordinator.ImportBrokerDeadLetterAsync(
      _import(messageId, body: "this is not json {{{"));

    await Assert.That(imported).IsTrue()
      .Because("a body that cannot even parse is precisely the one that needs forensic "
             + "custody — the import path must never lose a message to a parse failure");
  }

  [Test]
  public async Task Recover_BrokerRow_ReemitsIntoInboxAndMarksRecoveredAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    _ = await coordinator.ImportBrokerDeadLetterAsync(_import(messageId, streamId));

    var conn = await _openAsync(ctx);
    Guid dlqId;
    await using (var idCmd = conn.CreateCommand()) {
      idCmd.CommandText = "SELECT dead_letter_id FROM wh_dead_letters WHERE source_id = @id AND source_table = 'broker'";
      idCmd.Parameters.AddWithValue("id", messageId);
      dlqId = (Guid)(await idCmd.ExecuteScalarAsync())!;
    }

    bool recovered;
    await using (var recoverCmd = conn.CreateCommand()) {
      recoverCmd.CommandText = "SELECT recover_dead_letter(@id)";
      recoverCmd.Parameters.AddWithValue("id", dlqId);
      recovered = (bool)(await recoverCmd.ExecuteScalarAsync())!;
    }
    await Assert.That(recovered).IsTrue();

    await using (var inboxCmd = conn.CreateCommand()) {
      inboxCmd.CommandText = "SELECT event_data ->> 'v', stream_id, attempts FROM wh_inbox WHERE message_id = @id";
      inboxCmd.Parameters.AddWithValue("id", messageId);
      await using var reader = await inboxCmd.ExecuteReaderAsync();
      await Assert.That(await reader.ReadAsync()).IsTrue()
        .Because("recovery re-emits a broker row through the inbox front door so dispatch, "
               + "composite fan-out, and the internal max-attempts ladder all apply unchanged");
      await Assert.That(reader.GetString(0)).IsEqualTo("1");
      await Assert.That(reader.GetGuid(1)).IsEqualTo(streamId);
      await Assert.That(reader.GetInt32(2)).IsEqualTo(0);
    }

    await using (var statusCmd = conn.CreateCommand()) {
      statusCmd.CommandText = "SELECT recovery_status FROM wh_dead_letters WHERE dead_letter_id = @id";
      statusCmd.Parameters.AddWithValue("id", dlqId);
      await Assert.That((int)(await statusCmd.ExecuteScalarAsync())!).IsEqualTo(3)
        .Because("Recovered");
    }
  }

  /// <summary>
  /// A body that is not valid JSON is still taken into custody, verbatim.
  /// </summary>
  /// <remarks>
  /// Custody over correctness. A message reaches the broker DLQ precisely because something
  /// about it was wrong, and its body is often part of that — refusing to import it because it
  /// does not parse would discard the evidence at the exact moment an operator needs it. The
  /// function falls back to wrapping the raw text rather than casting it.
  /// </remarks>
  [Test]
  public async Task Import_WithABodyThatIsNotJson_StillTakesCustodyAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();

    var imported = await coordinator.ImportBrokerDeadLetterAsync(
      _import(messageId, body: "{not json at all"));

    await Assert.That(imported).IsTrue()
      .Because("a body that does not parse is exactly what an operator needs to see, not a "
             + "reason to drop the message");

    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_dead_letters WHERE source_id = @id AND source_table = 'broker'";
    cmd.Parameters.AddWithValue("id", messageId);
    await Assert.That((long)(await cmd.ExecuteScalarAsync())!).IsEqualTo(1L);
  }

  /// <summary>
  /// A failed import must throw, not report a duplicate.
  /// </summary>
  /// <remarks>
  /// The return value is load-bearing in a way that is easy to get backwards: FALSE means
  /// "custody already exists, safe to settle at the broker", so the drainer completes the broker
  /// message on it. A failed import that returned false would therefore complete the broker
  /// message with no custody anywhere — the message is gone. Throwing makes the drainer abandon,
  /// and the broker re-offers it on the next pass.
  /// </remarks>
  [Test]
  public async Task Import_WhenTheCallCannotBeMade_ThrowsRatherThanReportingADuplicateAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    // A wire message with no destination recorded: the call cannot be assembled, which stands
    // in for any failure between here and the store.
    var noDestination = new BrokerDeadLetterImport(
      MessageId: (Guid)TrackedGuid.NewMedo(),
      StreamId: null,
      MessageType: "Test.Message, Test",
      Destination: null!,
      EnvelopeJson: """{"v":1}""",
      BrokerReason: "MaxDeliveryAttemptsExceeded",
      BrokerDescription: null,
      EnqueuedAt: DateTimeOffset.UtcNow,
      DeliveryCount: 3);

    await Assert.That(async () => await coordinator.ImportBrokerDeadLetterAsync(noDestination))
      .ThrowsException()
      .Because("returning false would tell the drainer custody exists and let it settle the "
             + "broker message — losing it");
  }

  /// <summary>
  /// Re-importing the same wire message is a duplicate, and says so.
  /// </summary>
  [Test]
  public async Task Import_OfTheSameMessageTwice_ReportsADuplicateAsync() {
    // Idempotency on the wire message id is what lets the drainer retry safely: the second pass
    // must be told custody already exists so it settles the broker message instead of stacking
    // a second dead-letter row for the same failure.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();

    var first = await coordinator.ImportBrokerDeadLetterAsync(_import(messageId));
    var second = await coordinator.ImportBrokerDeadLetterAsync(_import(messageId));

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse()
      .Because("false is how the drainer learns it may settle the broker message");

    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_dead_letters WHERE source_id = @id AND source_table = 'broker'";
    cmd.Parameters.AddWithValue("id", messageId);
    await Assert.That((long)(await cmd.ExecuteScalarAsync())!).IsEqualTo(1L);
  }
}
