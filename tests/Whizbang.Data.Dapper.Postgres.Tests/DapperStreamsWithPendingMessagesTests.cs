using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// <see cref="DapperWorkCoordinator.GetStreamsWithPendingMessagesAsync"/>: which of the
/// given streams still have a message of a given type waiting. The saga sweep asks it before re-arming
/// a completion watchdog, so a wrong "nothing waiting" wakes a saga early or doubles its chain, and a
/// wrong "something waiting" leaves a stranded saga stranded.
/// </summary>
[NotInParallel(nameof(DapperStreamsWithPendingMessagesTests))]
[Category("Integration")]
public class DapperStreamsWithPendingMessagesTests : PostgresTestBase {
  private const string TICK_NAME = "Test.Sagas.WatchdogTick, Test.Sagas";
  private const string TICK_TYPE = "Test.Sagas.WatchdogTick, Test.Sagas, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
  private const string TICK_TYPE_WRAPPED =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Test.Sagas.WatchdogTick, Test.Sagas, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core";
  private const string OTHER_TYPE = "Contracts.OrderPlaced, Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

  private static Guid _stream() => (Guid)TrackedGuid.NewMedo();

  private static async Task _outboxAsync(NpgsqlConnection conn, Guid stream, string type, bool published, bool scheduled = false) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, scheduled_for, processed_at)
      VALUES (@id, 'topic', @type, 'TestEnvelope', '{}', '{}', 1, 0, NOW(), @stream, 0,
              @scheduled::timestamptz, @processed::timestamptz)";
    cmd.Parameters.AddWithValue("id", (Guid)TrackedGuid.NewMedo());
    cmd.Parameters.AddWithValue(nameof(type), type);
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.AddWithValue(nameof(scheduled), scheduled ? DateTime.UtcNow.AddHours(1) : DBNull.Value);
    cmd.Parameters.AddWithValue("processed", published ? DateTime.UtcNow : DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _inboxAsync(NpgsqlConnection conn, Guid stream, string type, bool finished, bool leased = false) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
        VALUES (@id, 'TestHandler', @type, '{}', '{}', NOW(), @stream)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts,
         partition_number, instance_id, lease_expiry, error, failure_reason, processed_at)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 1,
             0, @inst::uuid, @lease::timestamptz, NULL::text, 0, @processed::timestamptz
      FROM m";
    cmd.Parameters.AddWithValue("id", (Guid)TrackedGuid.NewMedo());
    cmd.Parameters.AddWithValue(nameof(type), type);
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.AddWithValue("inst", leased ? (Guid)TrackedGuid.NewMedo() : DBNull.Value);
    cmd.Parameters.AddWithValue("lease", leased ? DateTime.UtcNow.AddMinutes(5) : DBNull.Value);
    cmd.Parameters.AddWithValue("processed", finished ? DateTime.UtcNow : DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<IReadOnlySet<Guid>?> _askAsync(IReadOnlyList<Guid> streams) {
    var coordinator = new DapperWorkCoordinator(ConnectionString, new JsonSerializerOptions(), NullLogger<DapperWorkCoordinator>.Instance);
    return await coordinator.GetStreamsWithPendingMessagesAsync(streams, [TICK_NAME]);
  }

  [Test]
  public async Task Pending_ReturnsExactlyTheStreamsWithAWakeStillComingAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var unpublished = _stream();
    var scheduled = _stream();
    var beingHandled = _stream();
    var unclaimedInbox = _stream();
    var wrapped = _stream();
    var published = _stream();
    var handled = _stream();
    var otherType = _stream();
    await _outboxAsync(conn, unpublished, TICK_TYPE, published: false);
    await _outboxAsync(conn, scheduled, TICK_TYPE, published: false, scheduled: true);
    await _inboxAsync(conn, beingHandled, TICK_TYPE, finished: false, leased: true);
    await _inboxAsync(conn, unclaimedInbox, TICK_TYPE, finished: false);
    await _outboxAsync(conn, wrapped, TICK_TYPE_WRAPPED, published: false);
    await _outboxAsync(conn, published, TICK_TYPE, published: true);
    await _inboxAsync(conn, handled, TICK_TYPE, finished: true);
    await _outboxAsync(conn, otherType, OTHER_TYPE, published: false);

    var result = await _askAsync([unpublished, scheduled, beingHandled, unclaimedInbox, wrapped, published, handled, otherType]);

    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsEquivalentTo([unpublished, scheduled, beingHandled, unclaimedInbox, wrapped])
      .Because("a wake is coming while the tick is unpublished, scheduled for later, waiting in the inbox or being handled, in any stored form; a published or handled tick, or another message type, is no wake");
  }

  [Test]
  public async Task Pending_IgnoresStreamsNotAskedAboutAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var asked = _stream();
    var notAsked = _stream();
    await _outboxAsync(conn, notAsked, TICK_TYPE, published: false);

    var result = await _askAsync([asked]);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task Pending_WithNoStreams_ReturnsAnEmptySetAsync() {
    var result = await _askAsync([]);

    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsEmpty();
  }
}
