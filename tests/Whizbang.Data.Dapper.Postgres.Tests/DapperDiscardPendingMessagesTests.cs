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
/// <see cref="DapperWorkCoordinator.DiscardPendingInboxMessagesAsync"/> and
/// <see cref="DapperWorkCoordinator.DiscardPendingOutboxMessagesAsync"/>: the two halves of the maintenance
/// sweep behind "a feature that is off leaves nothing behind", on the Dapper driver. Containment match on
/// the normalized type name; leased rows and every other type are left alone.
/// </summary>
[Category("Integration")]
[NotInParallel(nameof(DapperDiscardPendingMessagesTests))]
public class DapperDiscardPendingMessagesTests : PostgresTestBase {
  private const string REQUEST_TYPE =
    "Whizbang.Core.Messaging.RequestRedeliveryCommand, Whizbang.Core, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null";
  private const string BUNDLE_TYPE_WRAPPED =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Minting.RedeliveryComposite, Whizbang.Core, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core";
  private const string CHECKPOINT_TYPE =
    "Whizbang.Core.Messaging.IntegrityCheckpoint, Whizbang.Core, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null";
  private const string REPORT_TYPE_WRAPPED =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Messaging.PerspectiveCoverageGapDetected, Whizbang.Core, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core";
  private const string DOMAIN_TYPE =
    "Contracts.OrderPlaced, Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

  private DapperWorkCoordinator _build()
    => new(ConnectionString, new JsonSerializerOptions(), NullLogger<DapperWorkCoordinator>.Instance);

  /// <summary>Seeds one pending row in <paramref name="table"/> (wh_inbox or wh_outbox), leased or not.</summary>
  private static async Task<Guid> _seedAsync(NpgsqlConnection conn, string table, string messageType, bool leased) {
    var id = (Guid)TrackedGuid.NewMedo();
    var sql = table == "wh_inbox"
      ? @"INSERT INTO wh_inbox
            (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
             stream_id, partition_number, instance_id, lease_expiry, error, failure_reason, scheduled_for)
          VALUES (@id, 'TestHandler', @type, '{}', '{}', 1, 1, NOW() - INTERVAL '1 hour',
                  @stream, 0, @inst, @lease, NULL, 0, NOW() + INTERVAL '30 days')"
      : @"INSERT INTO wh_outbox
            (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
             created_at, stream_id, partition_number, instance_id, lease_expiry)
          VALUES (@id, 'topic', @type, 'TestEnvelope', '{}', '{}', 1, 0, NOW() - INTERVAL '20 days',
                  @stream, 0, @inst, @lease)";
    await conn.ExecuteAsync(sql, new {
      id,
      type = messageType,
      stream = (Guid)TrackedGuid.NewMedo(),
      inst = leased ? (Guid?)(Guid)TrackedGuid.NewMedo() : null,
      lease = leased ? (DateTime?)DateTime.UtcNow.AddMinutes(5) : null,
    });
    return id;
  }

  private static async Task<bool> _existsAsync(NpgsqlConnection conn, string table, Guid messageId)
    => await conn.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} WHERE message_id = @m", new { m = messageId }) > 0;

  [Test]
  [Arguments("wh_inbox", REQUEST_TYPE, BUNDLE_TYPE_WRAPPED)]
  [Arguments("wh_outbox", CHECKPOINT_TYPE, REPORT_TYPE_WRAPPED)]
  public async Task Discard_DropsUnleasedRowsOfOffFeaturesInEveryStoredForm_LeavesTheRestAsync(
      string table, string plainType, string wrappedType) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var stalePlain = await _seedAsync(conn, table, plainType, leased: false);
    var staleWrapped = await _seedAsync(conn, table, wrappedType, leased: false);
    var leased = await _seedAsync(conn, table, plainType, leased: true);
    var domain = await _seedAsync(conn, table, DOMAIN_TYPE, leased: false);
    var options = new StreamIntegrityOptions { CheckpointsEnabled = false, PublishReportEvents = false };
    var types = table == "wh_inbox" ? IntegrityTraffic.InboxTypesToDiscard(options) : IntegrityTraffic.OutboxTypesToDiscard(options);

    var coordinator = _build();
    var discarded = table == "wh_inbox"
      ? await coordinator.DiscardPendingInboxMessagesAsync(types)
      : await coordinator.DiscardPendingOutboxMessagesAsync(types);

    await Assert.That(discarded).IsEqualTo(2L)
      .Because("the two unleased rows of features that are off go, whether stored with version metadata or wrapped in an envelope");
    await Assert.That(await _existsAsync(conn, table, stalePlain)).IsFalse();
    await Assert.That(await _existsAsync(conn, table, staleWrapped)).IsFalse();
    await Assert.That(await _existsAsync(conn, table, leased)).IsTrue()
      .Because("a leased row is mid-flight; the dispatch seam (inbox) or the publisher (outbox) owns it");
    await Assert.That(await _existsAsync(conn, table, domain)).IsTrue()
      .Because("only control-plane rows of features that are off are swept; domain traffic is never touched");
  }

  [Test]
  [Arguments("wh_inbox", REQUEST_TYPE)]
  [Arguments("wh_outbox", CHECKPOINT_TYPE)]
  public async Task Discard_EmptyList_IsANoOpAsync(string table, string type) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var stale = await _seedAsync(conn, table, type, leased: false);

    var coordinator = _build();
    var discarded = table == "wh_inbox"
      ? await coordinator.DiscardPendingInboxMessagesAsync([])
      : await coordinator.DiscardPendingOutboxMessagesAsync([]);

    await Assert.That(discarded).IsEqualTo(0L);
    await Assert.That(await _existsAsync(conn, table, stale)).IsTrue();
  }
}
