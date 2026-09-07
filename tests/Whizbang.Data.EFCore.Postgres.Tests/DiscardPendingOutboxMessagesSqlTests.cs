using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <see cref="EFCoreWorkCoordinator{TDbContext}.DiscardPendingOutboxMessagesAsync"/>: the outbox half of
/// the maintenance sweep behind "a feature that is off leaves nothing behind". A stored
/// <c>message_type</c> carries assembly version metadata and sometimes an envelope wrapper around the
/// normalized name, so the match is by containment; leased rows and every other type are left alone.
/// </summary>
[Category("Shard2")]
[NotInParallel(nameof(DiscardPendingOutboxMessagesSqlTests))]
public class DiscardPendingOutboxMessagesSqlTests : EFCoreTestBase {
  private const string CHECKPOINT_TYPE =
    "Whizbang.Core.Messaging.IntegrityCheckpoint, Whizbang.Core, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null";
  private const string REPORT_TYPE_WRAPPED =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Messaging.PerspectiveCoverageGapDetected, Whizbang.Core, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core";
  private const string DOMAIN_TYPE =
    "Contracts.OrderPlaced, Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

  private static async Task<Guid> _seedAsync(NpgsqlConnection conn, string messageType, bool leased) {
    var id = (Guid)TrackedGuid.NewMedo();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, instance_id, lease_expiry)
      VALUES (@id, 'topic', @type, 'TestEnvelope', '{}', '{}', 1, 0, NOW() - INTERVAL '20 days',
              @stream, 0, @inst, @lease)";
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("type", messageType);
    cmd.Parameters.AddWithValue("stream", (Guid)TrackedGuid.NewMedo());
    cmd.Parameters.AddWithValue("inst", leased ? (Guid)TrackedGuid.NewMedo() : DBNull.Value);
    cmd.Parameters.AddWithValue("lease", leased ? DateTime.UtcNow.AddMinutes(5) : DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
    return id;
  }

  private static async Task<bool> _existsAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    return (long)(await cmd.ExecuteScalarAsync())! > 0;
  }

  [Test]
  public async Task DiscardPendingOutboxMessagesAsync_DropsUnleasedRowsOfOffFeatures_LeavesTheRestAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var staleCheckpoint = await _seedAsync(conn, CHECKPOINT_TYPE, leased: false);
    var staleReport = await _seedAsync(conn, REPORT_TYPE_WRAPPED, leased: false);
    var leasedCheckpoint = await _seedAsync(conn, CHECKPOINT_TYPE, leased: true);
    var domain = await _seedAsync(conn, DOMAIN_TYPE, leased: false);
    var options = new StreamIntegrityOptions { CheckpointsEnabled = false, PublishReportEvents = false };

    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, new JsonSerializerOptions());
    var discarded = await coordinator.DiscardPendingOutboxMessagesAsync(IntegrityTraffic.OutboxTypesToDiscard(options));

    await Assert.That(discarded).IsEqualTo(2L)
      .Because("the unpublished checkpoint and the unpublished report belong to features that are off, whether stored with version metadata or wrapped in an envelope");
    await Assert.That(await _existsAsync(conn, staleCheckpoint)).IsFalse();
    await Assert.That(await _existsAsync(conn, staleReport)).IsFalse();
    await Assert.That(await _existsAsync(conn, leasedCheckpoint)).IsTrue()
      .Because("a leased row is being published right now; deleting under a live lease could race its completion");
    await Assert.That(await _existsAsync(conn, domain)).IsTrue()
      .Because("only control-plane rows of features that are off are swept; domain traffic is never touched");
  }

  [Test]
  public async Task DiscardPendingOutboxMessagesAsync_EmptyList_IsANoOpAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var staleCheckpoint = await _seedAsync(conn, CHECKPOINT_TYPE, leased: false);

    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, new JsonSerializerOptions());
    var discarded = await coordinator.DiscardPendingOutboxMessagesAsync([]);

    await Assert.That(discarded).IsEqualTo(0L);
    await Assert.That(await _existsAsync(conn, staleCheckpoint)).IsTrue();
  }
}
