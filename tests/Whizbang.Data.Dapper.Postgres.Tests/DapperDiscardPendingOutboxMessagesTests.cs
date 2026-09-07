using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// <see cref="DapperWorkCoordinator.DiscardPendingOutboxMessagesAsync"/>: the outbox half of the
/// maintenance sweep behind "a feature that is off leaves nothing behind", on the Dapper driver.
/// Containment match on the normalized type name; leased rows and every other type are left alone.
/// </summary>
[Category("Integration")]
[NotInParallel(nameof(DapperDiscardPendingOutboxMessagesTests))]
public class DapperDiscardPendingOutboxMessagesTests : PostgresTestBase {
  private const string CHECKPOINT_TYPE =
    "Whizbang.Core.Messaging.IntegrityCheckpoint, Whizbang.Core, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null";
  private const string REPORT_TYPE_WRAPPED =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Messaging.PerspectiveCoverageGapDetected, Whizbang.Core, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core";
  private const string DOMAIN_TYPE =
    "Contracts.OrderPlaced, Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

  private DapperWorkCoordinator _build()
    => new(ConnectionString, new JsonSerializerOptions(), NullLogger<DapperWorkCoordinator>.Instance);

  private static async Task<Guid> _seedAsync(NpgsqlConnection conn, string messageType, bool leased) {
    var id = (Guid)TrackedGuid.NewMedo();
    await conn.ExecuteAsync(@"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, instance_id, lease_expiry)
      VALUES (@id, 'topic', @type, 'TestEnvelope', '{}', '{}', 1, 0, NOW() - INTERVAL '20 days',
              @stream, 0, @inst, @lease)",
      new {
        id,
        type = messageType,
        stream = (Guid)TrackedGuid.NewMedo(),
        inst = leased ? (Guid?)(Guid)TrackedGuid.NewMedo() : null,
        lease = leased ? (DateTime?)DateTime.UtcNow.AddMinutes(5) : null,
      });
    return id;
  }

  private static async Task<bool> _existsAsync(NpgsqlConnection conn, Guid messageId)
    => await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_outbox WHERE message_id = @m", new { m = messageId }) > 0;

  [Test]
  public async Task DiscardPendingOutboxMessagesAsync_DropsUnleasedRowsOfOffFeatures_LeavesTheRestAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var staleCheckpoint = await _seedAsync(conn, CHECKPOINT_TYPE, leased: false);
    var staleReport = await _seedAsync(conn, REPORT_TYPE_WRAPPED, leased: false);
    var leasedCheckpoint = await _seedAsync(conn, CHECKPOINT_TYPE, leased: true);
    var domain = await _seedAsync(conn, DOMAIN_TYPE, leased: false);
    var options = new StreamIntegrityOptions { CheckpointsEnabled = false, PublishReportEvents = false };

    var discarded = await _build().DiscardPendingOutboxMessagesAsync(IntegrityTraffic.OutboxTypesToDiscard(options));

    await Assert.That(discarded).IsEqualTo(2L)
      .Because("the unpublished checkpoint and the unpublished report belong to features that are off");
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

    var discarded = await _build().DiscardPendingOutboxMessagesAsync([]);

    await Assert.That(discarded).IsEqualTo(0L);
    await Assert.That(await _existsAsync(conn, staleCheckpoint)).IsTrue();
  }
}
