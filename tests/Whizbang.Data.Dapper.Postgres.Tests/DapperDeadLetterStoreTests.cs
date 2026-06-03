using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// v0.502 slice C.3 — integration tests for <see cref="DapperDeadLetterStore"/>,
/// the Dapper wrapper around <c>move_to_dead_letters()</c>. Symmetric with
/// <see cref="Whizbang.Data.EFCore.Postgres.Tests.EFCoreDeadLetterStoreTests"/>;
/// the SQL function itself is covered by <c>MoveToDeadLettersSqlTests</c> in the EFCore
/// test project. These tests lock the Dapper wrapper's argument validation and the
/// round-trip mapping for the happy path.
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public class DapperDeadLetterStoreTests : PostgresTestBase {

  // ===== Constructor =====

  [Test]
  public async Task Constructor_NullConnectionString_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new DapperDeadLetterStore(
      connectionString: null!,
      logger: NullLogger<DapperDeadLetterStore>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullLogger_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new DapperDeadLetterStore(
      connectionString: ConnectionString,
      logger: null!))
      .Throws<ArgumentNullException>();
  }

  // ===== MoveAsync argument validation =====

  [Test]
  public async Task MoveAsync_NullSourceTable_ThrowsArgumentExceptionAsync() {
    var store = _newStore();

    await Assert.That(async () => await store.MoveAsync(
        deadLetterId: (Guid)TrackedGuid.NewMedo(),
        sourceTable: null!,
        sourceId: (Guid)TrackedGuid.NewMedo(),
        failureReason: MessageFailureReason.MaxAttemptsExceeded,
        errorText: "err",
        instanceId: (Guid)TrackedGuid.NewMedo(),
        generation: "v0.502"))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task MoveAsync_EmptySourceTable_ThrowsArgumentExceptionAsync() {
    var store = _newStore();

    await Assert.That(async () => await store.MoveAsync(
        deadLetterId: (Guid)TrackedGuid.NewMedo(),
        sourceTable: "",
        sourceId: (Guid)TrackedGuid.NewMedo(),
        failureReason: MessageFailureReason.MaxAttemptsExceeded,
        errorText: "err",
        instanceId: (Guid)TrackedGuid.NewMedo(),
        generation: "v0.502"))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task MoveAsync_NullGeneration_ThrowsArgumentExceptionAsync() {
    var store = _newStore();

    await Assert.That(async () => await store.MoveAsync(
        deadLetterId: (Guid)TrackedGuid.NewMedo(),
        sourceTable: DeadLetterSourceTable.OUTBOX,
        sourceId: (Guid)TrackedGuid.NewMedo(),
        failureReason: MessageFailureReason.MaxAttemptsExceeded,
        errorText: "err",
        instanceId: (Guid)TrackedGuid.NewMedo(),
        generation: null!))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task MoveAsync_EmptyGeneration_ThrowsArgumentExceptionAsync() {
    var store = _newStore();

    await Assert.That(async () => await store.MoveAsync(
        deadLetterId: (Guid)TrackedGuid.NewMedo(),
        sourceTable: DeadLetterSourceTable.OUTBOX,
        sourceId: (Guid)TrackedGuid.NewMedo(),
        failureReason: MessageFailureReason.MaxAttemptsExceeded,
        errorText: "err",
        instanceId: (Guid)TrackedGuid.NewMedo(),
        generation: ""))
      .Throws<ArgumentException>();
  }

  // ===== MoveAsync happy path =====

  [Test]
  public async Task MoveAsync_OutboxRow_ReturnsDeadLetterIdAndMovesRowAsync() {
    var store = _newStore();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _insertOutboxRowAsync(conn, messageId);

    var dlqId = (Guid)TrackedGuid.NewMedo();
    var result = await store.MoveAsync(
      deadLetterId: dlqId,
      sourceTable: DeadLetterSourceTable.OUTBOX,
      sourceId: messageId,
      failureReason: MessageFailureReason.Throttled,
      errorText: "throttle exhausted",
      instanceId: (Guid)TrackedGuid.NewMedo(),
      generation: "v0.502-dapper");

    await Assert.That(result).IsEqualTo(dlqId);

    var outboxCount = await conn.ExecuteScalarAsync<int>(
      "SELECT COUNT(*) FROM wh_outbox WHERE message_id = @id", new { id = messageId });
    await Assert.That(outboxCount).IsEqualTo(0);
    var dlqCount = await conn.ExecuteScalarAsync<int>(
      "SELECT COUNT(*) FROM wh_dead_letters WHERE dead_letter_id = @id", new { id = dlqId });
    await Assert.That(dlqCount).IsEqualTo(1);
  }

  [Test]
  public async Task MoveAsync_SourceRowAlreadyGone_ReturnsNullAsync() {
    var store = _newStore();

    var result = await store.MoveAsync(
      deadLetterId: (Guid)TrackedGuid.NewMedo(),
      sourceTable: DeadLetterSourceTable.OUTBOX,
      sourceId: (Guid)TrackedGuid.NewMedo(),  // never inserted
      failureReason: MessageFailureReason.MaxAttemptsExceeded,
      errorText: "ghost",
      instanceId: (Guid)TrackedGuid.NewMedo(),
      generation: "v0.502");

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task MoveAsync_NullErrorText_HandledByDapperAsync() {
    var store = _newStore();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _insertOutboxRowAsync(conn, messageId);

    var dlqId = (Guid)TrackedGuid.NewMedo();
    var result = await store.MoveAsync(
      deadLetterId: dlqId,
      sourceTable: DeadLetterSourceTable.OUTBOX,
      sourceId: messageId,
      failureReason: MessageFailureReason.Throttled,
      errorText: null,
      instanceId: (Guid)TrackedGuid.NewMedo(),
      generation: "v0.502-dapper-nullerr");

    await Assert.That(result).IsEqualTo(dlqId);
  }

  // ===== Helpers =====

  private DapperDeadLetterStore _newStore() =>
    new(ConnectionString, NullLogger<DapperDeadLetterStore>.Instance);

  private static async Task _insertOutboxRowAsync(NpgsqlConnection conn, Guid messageId) {
    await conn.ExecuteAsync(@"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{}', '{}', 1, 11, NOW(), @stream, 0)",
      new { msg = messageId, stream = (Guid)TrackedGuid.NewMedo() });
  }
}
