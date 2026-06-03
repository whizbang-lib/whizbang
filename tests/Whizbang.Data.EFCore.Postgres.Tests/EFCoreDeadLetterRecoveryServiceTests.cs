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
/// v0.502 slice C.7 — integration tests for <see cref="EFCoreDeadLetterRecoveryService{TDbContext}"/>,
/// the EFCore wrapper around the 6 recovery SQL functions in migration 051. The SQL
/// functions themselves are covered by <see cref="DeadLetterRecoverySqlTests"/>; these
/// tests lock the wrapper's constructor + round-trip mapping for each method.
/// </summary>
/// <docs>operations/dead-letter-queue/recovery</docs>
public class EFCoreDeadLetterRecoveryServiceTests : EFCoreTestBase {

  // ===== Constructor =====

  [Test]
  public async Task Constructor_NullDbContext_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>(
      dbContext: null!,
      logger: NullLogger<EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullLogger_ThrowsArgumentNullExceptionAsync() {
    await using var ctx = CreateDbContext();
    await Assert.That(() => new EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>(
      dbContext: ctx,
      logger: null!))
      .Throws<ArgumentNullException>();
  }

  // ===== FetchDueAsync =====

  [Test]
  public async Task FetchDueAsync_EmptyDlq_ReturnsEmptyListAsync() {
    await using var ctx = CreateDbContext();
    var svc = _newService(ctx);

    var result = await svc.FetchDueAsync(maxCount: 100);

    await Assert.That(result).IsNotNull();
    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchDueAsync_DueRow_ReturnsEntryWithAllFieldsPopulatedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var svc = _newService(ctx);

    var (dlqId, _) = await _seedDlqAsync(conn, generation: "v0.502-fetch");
    var due = await svc.FetchDueAsync(maxCount: 100);

    var entry = due.FirstOrDefault(e => e.DeadLetterId == dlqId);
    await Assert.That(entry).IsNotNull();
    await Assert.That(entry!.SourceTable).IsEqualTo("wh_outbox");
    await Assert.That(entry.Generation).IsEqualTo("v0.502-fetch");
    await Assert.That((int)entry.FailureReason).IsEqualTo(5);  // MaxAttemptsExceeded
    await Assert.That(entry.AttemptsWhenDlq).IsEqualTo(11);
    await Assert.That(entry.RecoveryAttempts).IsEqualTo(0);
    await Assert.That(entry.MessageType).IsEqualTo("TestEvent");
  }

  // ===== RecoverAsync =====

  [Test]
  public async Task RecoverAsync_ExistingRow_ReturnsTrueAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var svc = _newService(ctx);
    var (dlqId, _) = await _seedDlqAsync(conn);

    var ok = await svc.RecoverAsync(dlqId);

    await Assert.That(ok).IsTrue();
  }

  [Test]
  public async Task RecoverAsync_NonexistentRow_ReturnsFalseAsync() {
    await using var ctx = CreateDbContext();
    var svc = _newService(ctx);

    var ok = await svc.RecoverAsync((Guid)TrackedGuid.NewMedo());

    await Assert.That(ok).IsFalse();
  }

  // ===== MarkHoldingAsync =====

  [Test]
  public async Task MarkHoldingAsync_FlipsStatusToHoldForReviewAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var svc = _newService(ctx);
    var (dlqId, _) = await _seedDlqAsync(conn);

    await svc.MarkHoldingAsync(dlqId);

    var status = await _getStatusAsync(conn, dlqId);
    await Assert.That(status).IsEqualTo((int)DeadLetterRecoveryStatus.HoldForReview);
  }

  // ===== MarkPermanentlyFailedAsync =====

  [Test]
  public async Task MarkPermanentlyFailedAsync_FlipsStatusToPermanentlyFailedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var svc = _newService(ctx);
    var (dlqId, _) = await _seedDlqAsync(conn);

    await svc.MarkPermanentlyFailedAsync(dlqId);

    var status = await _getStatusAsync(conn, dlqId);
    await Assert.That(status).IsEqualTo((int)DeadLetterRecoveryStatus.PermanentlyFailed);
  }

  // ===== ScheduleNextAttemptAsync =====

  [Test]
  public async Task ScheduleNextAttemptAsync_CompletesWithoutErrorAsync() {
    // Coverage smoke for ScheduleNextAttemptAsync's wrapper round-trip. The persisted
    // effect (next_recovery_at column update) is locked by DeadLetterRecoverySqlTests at
    // the SQL function level; this test exercises the C# parameter wiring + Npgsql
    // round-trip without re-asserting the SQL semantic.
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var svc = _newService(ctx);
    var (dlqId, _) = await _seedDlqAsync(conn);
    var future = DateTimeOffset.UtcNow.AddHours(3);

    await svc.ScheduleNextAttemptAsync(dlqId, future);
    // No throw == wrapper path succeeded.
  }

  // ===== ResetForGenerationAsync =====

  [Test]
  public async Task ResetForGenerationAsync_NullGeneration_ThrowsArgumentExceptionAsync() {
    await using var ctx = CreateDbContext();
    var svc = _newService(ctx);

    await Assert.That(async () => await svc.ResetForGenerationAsync(null!))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task ResetForGenerationAsync_EmptyGeneration_ThrowsArgumentExceptionAsync() {
    await using var ctx = CreateDbContext();
    var svc = _newService(ctx);

    await Assert.That(async () => await svc.ResetForGenerationAsync(""))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task ResetForGenerationAsync_NewGeneration_SchedulesPendingRowsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var svc = _newService(ctx);
    // Seed a row dead-lettered under an OLDER generation so the new generation can schedule it.
    await _seedDlqAsync(conn, generation: "v0.500");

    var count = await svc.ResetForGenerationAsync("v0.502-newgen");

    await Assert.That(count).IsGreaterThanOrEqualTo(1)
      .Because("at least our seeded row should be eligible for the new generation");
  }

  // ===== Helpers =====

  private static EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext> _newService(WorkCoordinationDbContext ctx) =>
    new(ctx, NullLogger<EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>>.Instance);

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<(Guid DlqId, Guid OriginalMessageId)> _seedDlqAsync(
      NpgsqlConnection conn, string generation = "v0.502") {
    var dlqId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{}', '{}', 1, 11, NOW(), @stream, 0)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    await ins.ExecuteNonQueryAsync();

    await using var move = conn.CreateCommand();
    move.CommandText = "SELECT move_to_dead_letters(@dlq, @tbl, @src, @reason, @err, @inst, @gen)";
    move.Parameters.AddWithValue("dlq", dlqId);
    move.Parameters.AddWithValue("tbl", "wh_outbox");
    move.Parameters.AddWithValue("src", messageId);
    move.Parameters.AddWithValue("reason", 5);
    move.Parameters.AddWithValue("err", "seeded");
    move.Parameters.AddWithValue("inst", (Guid)TrackedGuid.NewMedo());
    move.Parameters.AddWithValue("gen", generation);
    await move.ExecuteNonQueryAsync();
    return (dlqId, messageId);
  }

  private static async Task<int> _getStatusAsync(NpgsqlConnection conn, Guid dlqId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT recovery_status FROM wh_dead_letters WHERE dead_letter_id = @id";
    cmd.Parameters.AddWithValue("id", dlqId);
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }
}
