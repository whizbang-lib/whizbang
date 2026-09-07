using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <see cref="EFCoreWorkCoordinator{TDbContext}.DiscardPendingInboxMessagesAsync"/>: the maintenance sweep
/// behind bilateral report-only. A stored <c>message_type</c> carries assembly version metadata and
/// sometimes an envelope wrapper around the normalized name, so the match is by containment; leased rows
/// and every other type are left alone.
/// </summary>
[Category("Shard1")]
[NotInParallel(nameof(DiscardPendingInboxMessagesSqlTests))]
public class DiscardPendingInboxMessagesSqlTests : EFCoreTestBase {
  private const string REQUEST_TYPE =
    "Whizbang.Core.Messaging.RequestRedeliveryCommand, Whizbang.Core, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null";
  private const string BUNDLE_TYPE_WRAPPED =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Minting.RedeliveryComposite, Whizbang.Core, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core";
  private const string DOMAIN_TYPE =
    "Contracts.OrderPlaced, Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

  private static async Task<Guid> _seedAsync(NpgsqlConnection conn, string messageType, bool leased, string scheduledForSql) {
    var id = (Guid)TrackedGuid.NewMedo();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $@"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         stream_id, partition_number, instance_id, lease_expiry, error, failure_reason, scheduled_for)
      VALUES (@id, 'TestHandler', @type, '{{}}', '{{}}', 1, 1, NOW() - INTERVAL '1 hour',
              @stream, 0, @inst, @lease, NULL, 0, {scheduledForSql})";
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
    cmd.CommandText = "SELECT count(*) FROM wh_inbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    return (long)(await cmd.ExecuteScalarAsync())! > 0;
  }

  [Test]
  public async Task DiscardPendingInboxMessagesAsync_DropsUnleasedRepairRowsInEveryStoredForm_LeavesTheRestAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var parkedRequest = await _seedAsync(conn, REQUEST_TYPE, leased: false, "NOW() + INTERVAL '30 days'");
    var dueBundle = await _seedAsync(conn, BUNDLE_TYPE_WRAPPED, leased: false, "NOW() - INTERVAL '1 minute'");
    var leasedRequest = await _seedAsync(conn, REQUEST_TYPE, leased: true, "NULL");
    var parkedDomain = await _seedAsync(conn, DOMAIN_TYPE, leased: false, "NOW() + INTERVAL '30 days'");

    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, new JsonSerializerOptions());
    var discarded = await coordinator.DiscardPendingInboxMessagesAsync(RepairTraffic.InboxMessageTypeNames);

    await Assert.That(discarded).IsEqualTo(2L)
      .Because("the parked request and the due bundle are unleased repair rows, whether stored with version metadata or wrapped in an envelope");
    await Assert.That(await _existsAsync(conn, parkedRequest)).IsFalse();
    await Assert.That(await _existsAsync(conn, dueBundle)).IsFalse();
    await Assert.That(await _existsAsync(conn, leasedRequest)).IsTrue()
      .Because("a leased row is mid-dispatch; the dispatch seam discards it under the same mode check");
    await Assert.That(await _existsAsync(conn, parkedDomain)).IsTrue()
      .Because("only repair traffic is swept; a parked domain retry is not repair");
  }

  [Test]
  public async Task DiscardPendingInboxMessagesAsync_EmptyList_IsANoOpAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var parkedRequest = await _seedAsync(conn, REQUEST_TYPE, leased: false, "NOW() + INTERVAL '30 days'");

    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, new JsonSerializerOptions());
    var discarded = await coordinator.DiscardPendingInboxMessagesAsync([]);

    await Assert.That(discarded).IsEqualTo(0L);
    await Assert.That(await _existsAsync(conn, parkedRequest)).IsTrue();
  }
}
