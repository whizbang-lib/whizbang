using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// v0.502 slice C.2 — integration tests for <see cref="EFCoreDeadLetterStore{TDbContext}"/>,
/// the thin EFCore wrapper around <c>move_to_dead_letters()</c>. The SQL function itself is
/// covered by <see cref="MoveToDeadLettersSqlTests"/>; these tests lock the wrapper's
/// argument validation surface and the round-trip mapping for the happy path.
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
[Category("Shard3")]
public class EFCoreDeadLetterStoreTests : EFCoreTestBase {

  // ===== Constructor =====

  [Test]
  public async Task Constructor_NullDbContext_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new EFCoreDeadLetterStore<WorkCoordinationDbContext>(
      dbContext: null!,
      logger: NullLogger<EFCoreDeadLetterStore<WorkCoordinationDbContext>>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullLogger_ThrowsArgumentNullExceptionAsync() {
    await using var ctx = CreateDbContext();
    await Assert.That(() => new EFCoreDeadLetterStore<WorkCoordinationDbContext>(
      dbContext: ctx,
      logger: null!))
      .Throws<ArgumentNullException>();
  }

  // ===== MoveAsync argument validation =====

  [Test]
  public async Task MoveAsync_NullSourceTable_ThrowsArgumentExceptionAsync() {
    await using var ctx = CreateDbContext();
    var store = _newStore(ctx);

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
    await using var ctx = CreateDbContext();
    var store = _newStore(ctx);

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
    await using var ctx = CreateDbContext();
    var store = _newStore(ctx);

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
    await using var ctx = CreateDbContext();
    var store = _newStore(ctx);

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
    await using var ctx = CreateDbContext();
    var store = _newStore(ctx);
    var conn = await _openAsync(ctx);
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
      generation: "v0.502-test");

    await Assert.That(result).IsEqualTo(dlqId)
      .Because("wrapper returns the dead_letter_id from the SQL function on success");

    // Row moved: outbox empty, dlq has the row.
    var outboxCount = await _countAsync(conn, "wh_outbox", "message_id", messageId);
    await Assert.That(outboxCount).IsEqualTo(0);
    var dlqCount = await _countAsync(conn, "wh_dead_letters", "dead_letter_id", dlqId);
    await Assert.That(dlqCount).IsEqualTo(1);
  }

  [Test]
  public async Task MoveAsync_SourceRowAlreadyGone_ReturnsNullAsync() {
    await using var ctx = CreateDbContext();
    var store = _newStore(ctx);

    // Try to move a row that never existed — idempotency path.
    var result = await store.MoveAsync(
      deadLetterId: (Guid)TrackedGuid.NewMedo(),
      sourceTable: DeadLetterSourceTable.OUTBOX,
      sourceId: (Guid)TrackedGuid.NewMedo(),  // never inserted
      failureReason: MessageFailureReason.MaxAttemptsExceeded,
      errorText: "ghost",
      instanceId: (Guid)TrackedGuid.NewMedo(),
      generation: "v0.502");

    await Assert.That(result).IsNull()
      .Because("wrapper returns null when SQL fn returns NULL (source row already gone)");
  }

  [Test]
  public async Task MoveAsync_NullErrorText_HandledAsDBNullAsync() {
    // The wrapper substitutes DBNull.Value for a null errorText so the SQL function
    // accepts it. Locking that no NRE / Npgsql-error fires on a null errorText.
    await using var ctx = CreateDbContext();
    var store = _newStore(ctx);
    var conn = await _openAsync(ctx);
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
      generation: "v0.502-nullerr");

    await Assert.That(result).IsEqualTo(dlqId);
  }

  // ===== Helpers =====

  private static EFCoreDeadLetterStore<WorkCoordinationDbContext> _newStore(WorkCoordinationDbContext ctx) =>
    new(ctx, NullLogger<EFCoreDeadLetterStore<WorkCoordinationDbContext>>.Instance);

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _insertOutboxRowAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{}', '{}', 1, 11, NOW(), @stream, 0)";
    cmd.Parameters.AddWithValue("msg", messageId);
    cmd.Parameters.AddWithValue("stream", (Guid)TrackedGuid.NewMedo());
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int> _countAsync(NpgsqlConnection conn, string table, string column, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = @id";
    cmd.Parameters.AddWithValue("id", id);
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }
}
