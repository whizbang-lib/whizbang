using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Locks the v0.657 slice 5c invariants around
/// <see cref="EFCoreWorkCoordinator{TDbContext}.FindStuckOutboxRowsAsync"/> and
/// <see cref="EFCoreWorkCoordinator{TDbContext}.FindStuckInboxRowsAsync"/>.
/// The default <see cref="Whizbang.Core.Messaging.IWorkCoordinator"/> impl returns
/// an empty list; the Postgres backend MUST override to delegate to the
/// <c>find_stuck_outbox_rows</c> / <c>find_stuck_inbox_rows</c> SQL functions
/// introduced in migration 054.
/// </summary>
/// <docs>operations/observability/stuck-row-sentinel</docs>
public class EFCoreFindStuckRowsTests : EFCoreTestBase {

  /// <summary>
  /// Inserts a stuck wh_outbox row directly via SQL; calls the coordinator's
  /// FindStuckOutboxRowsAsync; asserts the row surfaces with all forensic
  /// fields populated.
  /// </summary>
  [Test]
  public async Task FindStuckOutboxRows_PostgresBacking_DelegatesToSqlFunctionAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    Guid messageId = TrackedGuid.NewMedo();
    Guid streamId = TrackedGuid.NewMedo();
    const string messageType = "a consumer.RemoveShellUserCommand";
    await _insertOutboxRowAsync(conn, messageId, streamId, messageType, attempts: 992);

    var coordinator = _buildCoordinator(ctx);
    var stuck = await coordinator.FindStuckOutboxRowsAsync(maxAttempts: 10, limit: 50);

    await Assert.That(stuck.Count).IsEqualTo(1);
    await Assert.That(stuck[0].MessageId).IsEqualTo(messageId);
    await Assert.That(stuck[0].MessageType).IsEqualTo(messageType)
      .Because("MessageType is the producer-side hint — must survive the round-trip through SQL projection.");
    await Assert.That(stuck[0].StreamId).IsEqualTo(streamId);
    await Assert.That(stuck[0].Attempts).IsEqualTo(992);
  }

  /// <summary>
  /// Mirror invariant for inbox.
  /// </summary>
  [Test]
  public async Task FindStuckInboxRows_PostgresBacking_DelegatesToSqlFunctionAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    Guid messageId = TrackedGuid.NewMedo();
    Guid streamId = TrackedGuid.NewMedo();
    await _insertInboxRowAsync(conn, messageId, streamId, "a consumer.SampleEvent", attempts: 15);

    var coordinator = _buildCoordinator(ctx);
    var stuck = await coordinator.FindStuckInboxRowsAsync(maxAttempts: 10, limit: 50);

    await Assert.That(stuck.Count).IsEqualTo(1);
    await Assert.That(stuck[0].MessageId).IsEqualTo(messageId);
  }

  /// <summary>
  /// Healthy table returns empty list — no false positives.
  /// </summary>
  [Test]
  public async Task FindStuckOutboxRows_HealthyTable_ReturnsEmptyAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _buildCoordinator(ctx);

    var stuck = await coordinator.FindStuckOutboxRowsAsync(maxAttempts: 10, limit: 50);

    await Assert.That(stuck).IsEmpty();
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _buildCoordinator(WorkCoordinationDbContext ctx)
    => new(ctx, new JsonSerializerOptions());

  private static async Task _insertOutboxRowAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, string messageType, int attempts) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number)
      VALUES (@msg, 'topic', @type, 'TestEnvelope', '{}', '{}', 1, @attempts, NOW(), @stream, 0)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("type", messageType);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("attempts", attempts);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertInboxRowAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, string messageType, int attempts) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts,
         received_at, stream_id, partition_number)
      VALUES (@msg, 'TestHandler', @type, '{}', '{}', 1, @attempts, NOW(), @stream, 0)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("type", messageType);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("attempts", attempts);
    await ins.ExecuteNonQueryAsync();
  }
}
