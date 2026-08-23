using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// DB round-trip tests for the lifecycle-reconciliation and janitor surface of
/// <see cref="EFCoreWorkCoordinator{TDbContext}"/>:
/// <c>GetOrphanedLifecycleEventsAsync</c> / <c>CleanupLifecycleCompletionsAsync</c> (mig 035),
/// <c>GetCursorsRequiringRewindAsync</c> (RewindRequired flag, bit 5 = 32),
/// <c>PurgeOrphanInboxAsync</c> (mig 042/044), <c>NotifyScheduledRetryDueAsync</c> (mig 049),
/// <c>CleanupCompletedStreamsAsync</c> (mig 023), and <c>GetLocalServiceIdAsync</c>
/// (wh_service_config singleton from mig 046).
/// Each test seeds real rows via raw SQL, calls the coordinator method, and asserts both
/// the returned data AND the resulting table state.
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-reconciliation</docs>
[Category("Shard4")]
public class EFCoreWorkCoordinatorLifecycleAndJanitorTests : EFCoreTestBase {
  private static readonly string[] _twoPerspectives = ["P.One", "P.Two"];
  private static readonly string[] _onePerspective = ["P.One"];


  // --------------------------------------------------------------------------
  // NotifyScheduledRetryDueAsync
  // --------------------------------------------------------------------------

  [Test]
  public async Task NotifyScheduledRetryDueAsync_DueOutboxAndInboxRows_ReturnsDistinctStreamCountAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    // Two distinct outbox streams due for retry + one inbox stream due for retry → SUM = 3.
    var outboxStreamA = Guid.NewGuid();
    var outboxStreamB = Guid.NewGuid();
    var inboxStream = Guid.NewGuid();
    var due = DateTimeOffset.UtcNow.AddMinutes(-1);
    await _insertOutboxRowAsync(connection, Guid.NewGuid(), outboxStreamA, scheduledFor: due);
    await _insertOutboxRowAsync(connection, Guid.NewGuid(), outboxStreamA, scheduledFor: due); // same stream — DISTINCT collapses it
    await _insertOutboxRowAsync(connection, Guid.NewGuid(), outboxStreamB, scheduledFor: due);
    await _insertInboxRowAsync(connection, Guid.NewGuid(), "TestEvent", inboxStream, scheduledFor: due);

    var coordinator = _createCoordinator(dbContext);
    var notified = await coordinator.NotifyScheduledRetryDueAsync();

    await Assert.That(notified).IsEqualTo(3);

    // NOTIFY emission must not mutate the work rows — they stay pending for the claim path.
    var pendingOutbox = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_outbox WHERE processed_at IS NULL AND scheduled_for IS NOT NULL");
    var pendingInbox = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_inbox WHERE processed_at IS NULL AND scheduled_for IS NOT NULL");
    await Assert.That(pendingOutbox).IsEqualTo(3L);
    await Assert.That(pendingInbox).IsEqualTo(1L);
  }

  [Test]
  public async Task NotifyScheduledRetryDueAsync_NoQualifyingRows_ReturnsZeroAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    var past = DateTimeOffset.UtcNow.AddMinutes(-5);
    // Non-qualifying shapes: no schedule, future schedule, already processed, stream-less.
    await _insertOutboxRowAsync(connection, Guid.NewGuid(), Guid.NewGuid(), scheduledFor: null);
    await _insertOutboxRowAsync(connection, Guid.NewGuid(), Guid.NewGuid(), scheduledFor: DateTimeOffset.UtcNow.AddHours(1));
    await _insertOutboxRowAsync(connection, Guid.NewGuid(), Guid.NewGuid(), scheduledFor: past, processedAt: DateTimeOffset.UtcNow);
    await _insertOutboxRowAsync(connection, Guid.NewGuid(), streamId: null, scheduledFor: past);

    var coordinator = _createCoordinator(dbContext);
    var notified = await coordinator.NotifyScheduledRetryDueAsync();

    await Assert.That(notified).IsEqualTo(0);

    // All four seeded rows must be untouched.
    var total = await _countAsync(connection, "SELECT COUNT(*) FROM wh_outbox");
    await Assert.That(total).IsEqualTo(4L);
  }

  // --------------------------------------------------------------------------
  // CleanupCompletedStreamsAsync
  // --------------------------------------------------------------------------

  [Test]
  public async Task CleanupCompletedStreamsAsync_MixedPendingAndCompleted_EvictsOnlyCompletedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    var completedStream = Guid.NewGuid(); // active-streams row, no pending work anywhere
    var busyStream = Guid.NewGuid();      // active-streams row with a pending outbox row
    await _insertActiveStreamAsync(connection, completedStream);
    await _insertActiveStreamAsync(connection, busyStream);
    await _insertOutboxRowAsync(connection, Guid.NewGuid(), busyStream);

    var coordinator = _createCoordinator(dbContext);
    var evicted = await coordinator.CleanupCompletedStreamsAsync([completedStream, busyStream]);

    await Assert.That(evicted).IsEqualTo(1);

    var completedRemains = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = @id", ("id", completedStream));
    var busyRemains = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = @id", ("id", busyStream));
    await Assert.That(completedRemains).IsEqualTo(0L);
    await Assert.That(busyRemains).IsEqualTo(1L);
  }

  [Test]
  public async Task CleanupCompletedStreamsAsync_EmptyStreamList_ReturnsZeroWithoutTouchingRowsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    var streamId = Guid.NewGuid();
    await _insertActiveStreamAsync(connection, streamId);

    var coordinator = _createCoordinator(dbContext);
    var evicted = await coordinator.CleanupCompletedStreamsAsync([]);

    await Assert.That(evicted).IsEqualTo(0);

    var remains = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = @id", ("id", streamId));
    await Assert.That(remains).IsEqualTo(1L);
  }

  // --------------------------------------------------------------------------
  // GetOrphanedLifecycleEventsAsync
  // --------------------------------------------------------------------------

  [Test]
  public async Task GetOrphanedLifecycleEventsAsync_AllPerspectivesCaughtUpNoCompletionMarker_ReturnsEventAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _insertEventStoreRowAsync(
      connection, eventId, streamId, "Whizbang.Tests.OrphanEvent",
      eventData: "{\"answer\":42}", scope: "{\"t\":\"tenant-a\"}");
    // Both expected perspectives have cursors at (>=) the event — lifecycle should have fired.
    await _insertCursorAsync(connection, streamId, "P.One", eventId, status: 1);
    await _insertCursorAsync(connection, streamId, "P.Two", eventId, status: 1);

    var coordinator = _createCoordinator(dbContext);
    var orphans = await coordinator.GetOrphanedLifecycleEventsAsync(
      new Dictionary<string, IReadOnlyList<string>> {
        ["Whizbang.Tests.OrphanEvent"] = _twoPerspectives
      },
      TimeSpan.FromHours(1));

    await Assert.That(orphans.Count).IsEqualTo(1);
    await Assert.That(orphans[0].EventId).IsEqualTo(eventId);
    await Assert.That(orphans[0].StreamId).IsEqualTo(streamId);

    var envelope = (MessageEnvelope<JsonElement>)orphans[0].Envelope;
    await Assert.That(envelope.MessageId.Value).IsEqualTo(eventId);
    await Assert.That(envelope.Payload.GetProperty("answer").GetInt32()).IsEqualTo(42);
    // Scope short-key "t" (tenant) must be restored as a security-context hop.
    await Assert.That(envelope.Hops.Count).IsEqualTo(1);

    // Read-only reconciliation query — event row must survive untouched.
    var eventRemains = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_event_store WHERE event_id = @id", ("id", eventId));
    await Assert.That(eventRemains).IsEqualTo(1L);
  }

  [Test]
  public async Task GetOrphanedLifecycleEventsAsync_CompletionMarkerExists_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _insertEventStoreRowAsync(connection, eventId, streamId, "Whizbang.Tests.OrphanEvent");
    await _insertCursorAsync(connection, streamId, "P.One", eventId, status: 1);
    // PostLifecycle already fired for this event — durable marker present.
    await _insertLifecycleCompletionAsync(connection, eventId, DateTimeOffset.UtcNow);

    var coordinator = _createCoordinator(dbContext);
    var orphans = await coordinator.GetOrphanedLifecycleEventsAsync(
      new Dictionary<string, IReadOnlyList<string>> {
        ["Whizbang.Tests.OrphanEvent"] = _onePerspective
      },
      TimeSpan.FromHours(1));

    await Assert.That(orphans).IsEmpty();
  }

  [Test]
  public async Task GetOrphanedLifecycleEventsAsync_PerspectiveStillLagging_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _insertEventStoreRowAsync(connection, eventId, streamId, "Whizbang.Tests.OrphanEvent");
    // Only one of the two expected perspectives has caught up — not orphaned yet.
    await _insertCursorAsync(connection, streamId, "P.One", eventId, status: 1);

    var coordinator = _createCoordinator(dbContext);
    var orphans = await coordinator.GetOrphanedLifecycleEventsAsync(
      new Dictionary<string, IReadOnlyList<string>> {
        ["Whizbang.Tests.OrphanEvent"] = _twoPerspectives
      },
      TimeSpan.FromHours(1));

    await Assert.That(orphans).IsEmpty();
  }

  [Test]
  public async Task GetOrphanedLifecycleEventsAsync_GlobalCapBoundsBatchAcrossTypesAsync() {
    // The reconcile batch is capped GLOBALLY (oldest first) across every registered type — the
    // per-type query loop it replaces had a per-type limit but no overall bound, and over a
    // thousand sequential per-type round-trips could stall a large host past its liveness budget.
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    var s1 = (Guid)TrackedGuid.NewMedo();
    var s2 = (Guid)TrackedGuid.NewMedo();
    var s3 = (Guid)TrackedGuid.NewMedo();
    var e1 = (Guid)TrackedGuid.NewMedo();
    var e2 = (Guid)TrackedGuid.NewMedo();
    var e3 = (Guid)TrackedGuid.NewMedo();
    await _insertEventStoreRowAsync(connection, e1, s1, "Whizbang.Tests.OrphanEvent");
    await _insertEventStoreRowAsync(connection, e2, s2, "Whizbang.Tests.OtherOrphanEvent");
    await _insertEventStoreRowAsync(connection, e3, s3, "Whizbang.Tests.OrphanEvent");
    await _insertCursorAsync(connection, s1, "P.One", e1, status: 1);
    await _insertCursorAsync(connection, s2, "P.One", e2, status: 1);
    await _insertCursorAsync(connection, s3, "P.One", e3, status: 1);

    var map = new Dictionary<string, IReadOnlyList<string>> {
      ["Whizbang.Tests.OrphanEvent"] = _onePerspective,
      ["Whizbang.Tests.OtherOrphanEvent"] = _onePerspective,
    };

    var coordinator = _createCoordinator(dbContext);
    var capped = await coordinator.GetOrphanedLifecycleEventsAsync(map, TimeSpan.FromHours(1), maxOrphans: 2);
    var all = await coordinator.GetOrphanedLifecycleEventsAsync(map, TimeSpan.FromHours(1));

    await Assert.That(all.Count).IsEqualTo(3)
      .Because("one set-based pass finds qualifying orphans across EVERY registered type.");
    await Assert.That(capped.Count).IsEqualTo(2)
      .Because("maxOrphans bounds the whole batch, not each type — the caller drains via " +
               "repeated bounded passes.");
  }

  [Test]
  public async Task GetOrphanedLifecycleEventsAsync_EmptyOrPerspectivelessMap_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    // A fully-qualifying orphan exists, but the request maps don't ask about it.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _insertEventStoreRowAsync(connection, eventId, streamId, "Whizbang.Tests.OrphanEvent");
    await _insertCursorAsync(connection, streamId, "P.One", eventId, status: 1);

    var coordinator = _createCoordinator(dbContext);

    // Branch 1: empty dictionary short-circuits before any SQL.
    var noTypes = await coordinator.GetOrphanedLifecycleEventsAsync(
      new Dictionary<string, IReadOnlyList<string>>(), TimeSpan.FromHours(1));
    await Assert.That(noTypes).IsEmpty();

    // Branch 2: event type registered with zero expected perspectives is skipped.
    var noPerspectives = await coordinator.GetOrphanedLifecycleEventsAsync(
      new Dictionary<string, IReadOnlyList<string>> {
        ["Whizbang.Tests.OrphanEvent"] = Array.Empty<string>()
      },
      TimeSpan.FromHours(1));
    await Assert.That(noPerspectives).IsEmpty();
  }

  // --------------------------------------------------------------------------
  // CleanupLifecycleCompletionsAsync
  // --------------------------------------------------------------------------

  [Test]
  public async Task CleanupLifecycleCompletionsAsync_MarkersOlderThanRetention_DeletesOnlyExpiredAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    var expiredEventId = Guid.NewGuid();
    var freshEventId = Guid.NewGuid();
    await _insertLifecycleCompletionAsync(connection, expiredEventId, DateTimeOffset.UtcNow.AddDays(-10));
    await _insertLifecycleCompletionAsync(connection, freshEventId, DateTimeOffset.UtcNow);

    var coordinator = _createCoordinator(dbContext);
    var deleted = await coordinator.CleanupLifecycleCompletionsAsync(TimeSpan.FromDays(7));

    await Assert.That(deleted).IsEqualTo(1);

    var expiredRemains = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_lifecycle_completions WHERE event_id = @id", ("id", expiredEventId));
    var freshRemains = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_lifecycle_completions WHERE event_id = @id", ("id", freshEventId));
    await Assert.That(expiredRemains).IsEqualTo(0L);
    await Assert.That(freshRemains).IsEqualTo(1L);
  }

  [Test]
  public async Task CleanupLifecycleCompletionsAsync_AllMarkersWithinRetention_ReturnsZeroAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    await _insertLifecycleCompletionAsync(connection, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1));
    await _insertLifecycleCompletionAsync(connection, Guid.NewGuid(), DateTimeOffset.UtcNow);

    var coordinator = _createCoordinator(dbContext);
    var deleted = await coordinator.CleanupLifecycleCompletionsAsync(TimeSpan.FromDays(7));

    await Assert.That(deleted).IsEqualTo(0);

    var remaining = await _countAsync(connection, "SELECT COUNT(*) FROM wh_lifecycle_completions");
    await Assert.That(remaining).IsEqualTo(2L);
  }

  // --------------------------------------------------------------------------
  // GetCursorsRequiringRewindAsync
  // --------------------------------------------------------------------------

  [Test]
  public async Task GetCursorsRequiringRewindAsync_FlaggedCursors_ReturnsThemInOrderWithFieldsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _insertEventStoreRowAsync(connection, eventId, streamId, "T");

    // status 33 = Completed(1) | RewindRequired(32); status 32 = RewindRequired alone.
    await _insertCursorAsync(connection, streamId, "A.First", eventId, status: 33, rewindTriggerEventId: eventId);
    await _insertCursorAsync(connection, streamId, "B.Second", lastEventId: null, status: 32);
    // Unflagged cursor on another stream must not appear.
    await _insertCursorAsync(connection, (Guid)TrackedGuid.NewMedo(), "C.Third", lastEventId: null, status: 1);

    var coordinator = _createCoordinator(dbContext);
    var cursors = await coordinator.GetCursorsRequiringRewindAsync();

    await Assert.That(cursors.Count).IsEqualTo(2);

    // ORDER BY stream_id, perspective_name — same stream, so name order decides.
    await Assert.That(cursors[0].StreamId).IsEqualTo(streamId);
    await Assert.That(cursors[0].PerspectiveName).IsEqualTo("A.First");
    await Assert.That(cursors[0].LastEventId).IsEqualTo(eventId);
    await Assert.That(cursors[0].RewindTriggerEventId).IsEqualTo(eventId);

    await Assert.That(cursors[1].StreamId).IsEqualTo(streamId);
    await Assert.That(cursors[1].PerspectiveName).IsEqualTo("B.Second");
    await Assert.That(cursors[1].LastEventId).IsNull();
    await Assert.That(cursors[1].RewindTriggerEventId).IsNull();

    // Startup scan is read-only — the flag must remain set for the repair path to consume.
    var stillFlagged = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_perspective_cursors WHERE (status & 32) = 32");
    await Assert.That(stillFlagged).IsEqualTo(2L);
  }

  [Test]
  public async Task GetCursorsRequiringRewindAsync_NoFlaggedCursors_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    // Statuses without bit 5: Completed(1), Failed(2), and a higher non-rewind bit (16).
    await _insertCursorAsync(connection, (Guid)TrackedGuid.NewMedo(), "P.One", lastEventId: null, status: 1);
    await _insertCursorAsync(connection, (Guid)TrackedGuid.NewMedo(), "P.Two", lastEventId: null, status: 2);
    await _insertCursorAsync(connection, (Guid)TrackedGuid.NewMedo(), "P.Three", lastEventId: null, status: 16);

    var coordinator = _createCoordinator(dbContext);
    var cursors = await coordinator.GetCursorsRequiringRewindAsync();

    await Assert.That(cursors).IsEmpty();
  }

  // --------------------------------------------------------------------------
  // GetLocalServiceIdAsync
  // --------------------------------------------------------------------------

  [Test]
  public async Task GetLocalServiceIdAsync_ConfigRowSeededByMigration_ReturnsStoredServiceIdAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    // Migration 046 seeds the wh_service_config singleton with a generated service_id.
    Guid expected;
    await using (var read = connection.CreateCommand()) {
      read.CommandText = "SELECT service_id FROM wh_service_config LIMIT 1";
      expected = (Guid)(await read.ExecuteScalarAsync())!;
    }

    var coordinator = _createCoordinator(dbContext);
    var serviceId = await coordinator.GetLocalServiceIdAsync();

    await Assert.That(serviceId).IsNotEqualTo(Guid.Empty);
    await Assert.That(serviceId).IsEqualTo(expected);
  }

  [Test]
  public async Task GetLocalServiceIdAsync_NoConfigRow_ReturnsEmptyGuidAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    await using (var wipe = connection.CreateCommand()) {
      wipe.CommandText = "DELETE FROM wh_service_config";
      await wipe.ExecuteNonQueryAsync();
    }

    var coordinator = _createCoordinator(dbContext);
    var serviceId = await coordinator.GetLocalServiceIdAsync();

    await Assert.That(serviceId).IsEqualTo(Guid.Empty);
  }

  // --------------------------------------------------------------------------
  // PurgeOrphanInboxAsync
  // --------------------------------------------------------------------------

  [Test]
  public async Task PurgeOrphanInboxAsync_UnhandledUnleasedRows_DeletesAndReturnsThemAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    var orphanId = Guid.NewGuid();
    var handledId = Guid.NewGuid();
    var leasedOrphanId = Guid.NewGuid();
    // Orphan: type not handled locally, unleased, unprocessed → purged.
    await _insertInboxRowAsync(connection, orphanId, "Ghost.Type", Guid.NewGuid());
    // Handled type → survives.
    await _insertInboxRowAsync(connection, handledId, "Known.Type", Guid.NewGuid());
    // Orphan type but leased (instance_id set) → survives; the in-flight handler owns it.
    await _insertInboxRowAsync(connection, leasedOrphanId, "Ghost.Type", Guid.NewGuid(), instanceId: Guid.NewGuid());

    var coordinator = _createCoordinator(dbContext);
    var purged = await coordinator.PurgeOrphanInboxAsync(["Known.Type"]);

    await Assert.That(purged.Count).IsEqualTo(1);
    await Assert.That(purged[0].MessageId).IsEqualTo(orphanId);
    await Assert.That(purged[0].MessageType).IsEqualTo("Ghost.Type");
    await Assert.That(purged[0].HandlerName).IsEqualTo("TestHandler");

    var orphanRemains = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_inbox WHERE message_id = @id", ("id", orphanId));
    var handledRemains = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_inbox WHERE message_id = @id", ("id", handledId));
    var leasedRemains = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_inbox WHERE message_id = @id", ("id", leasedOrphanId));
    await Assert.That(orphanRemains).IsEqualTo(0L);
    await Assert.That(handledRemains).IsEqualTo(1L);
    await Assert.That(leasedRemains).IsEqualTo(1L);
  }

  [Test]
  public async Task PurgeOrphanInboxAsync_EmptyHandledTypes_IsSafeNoOpAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);

    // Cold-start guard: empty registry must never truncate the inbox.
    await _insertInboxRowAsync(connection, Guid.NewGuid(), "Ghost.Type", Guid.NewGuid());
    await _insertInboxRowAsync(connection, Guid.NewGuid(), "Other.Type", Guid.NewGuid());

    var coordinator = _createCoordinator(dbContext);
    var purged = await coordinator.PurgeOrphanInboxAsync([]);

    await Assert.That(purged).IsEmpty();

    var remaining = await _countAsync(connection, "SELECT COUNT(*) FROM wh_inbox");
    await Assert.That(remaining).IsEqualTo(2L);
  }

  // --------------------------------------------------------------------------
  // Helpers
  // --------------------------------------------------------------------------

  private static async Task<NpgsqlConnection> _openConnectionAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _createCoordinator(WorkCoordinationDbContext dbContext) {
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
  }

  private static async Task<long> _countAsync(
      NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _insertOutboxRowAsync(
      NpgsqlConnection connection, Guid messageId, Guid? streamId,
      DateTimeOffset? scheduledFor = null, DateTimeOffset? processedAt = null) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, scheduled_for, processed_at)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{""payload"":1}', '{""hop"":1}', 1, 0,
              NOW(), @stream, 0, @scheduled, @processed)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", (object?)streamId ?? DBNull.Value);
    ins.Parameters.Add(new NpgsqlParameter("scheduled", NpgsqlDbType.TimestampTz) { Value = (object?)scheduledFor ?? DBNull.Value });
    ins.Parameters.Add(new NpgsqlParameter("processed", NpgsqlDbType.TimestampTz) { Value = (object?)processedAt ?? DBNull.Value });
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertInboxRowAsync(
      NpgsqlConnection connection, Guid messageId, string messageType, Guid? streamId,
      Guid? instanceId = null, DateTimeOffset? scheduledFor = null) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         instance_id, lease_expiry, stream_id, partition_number, scheduled_for)
      VALUES (@msg, 'TestHandler', @type, '{""payload"":1}', '{""hop"":1}', 1, 0, NOW(),
              @inst, @lease, @stream, 0, @scheduled)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("type", messageType);
    ins.Parameters.AddWithValue("stream", (object?)streamId ?? DBNull.Value);
    ins.Parameters.AddWithValue("inst", (object?)instanceId ?? DBNull.Value);
    ins.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) {
      Value = instanceId.HasValue ? DateTimeOffset.UtcNow.AddMinutes(5) : DBNull.Value
    });
    ins.Parameters.Add(new NpgsqlParameter("scheduled", NpgsqlDbType.TimestampTz) { Value = (object?)scheduledFor ?? DBNull.Value });
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertActiveStreamAsync(NpgsqlConnection connection, Guid streamId) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_active_streams (stream_id, partition_number)
      VALUES (@stream, 0)";
    ins.Parameters.AddWithValue("stream", streamId);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertEventStoreRowAsync(
      NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType,
      string eventData = "{}", string scope = "{}") {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
      VALUES (@evt, @stream, @stream, 'agg', @type, @scope::jsonb, 1, NOW());
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      VALUES (@evt, @data::jsonb, '{}'::jsonb)";
    ins.Parameters.AddWithValue("evt", eventId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("type", eventType);
    ins.Parameters.AddWithValue("data", eventData);
    ins.Parameters.AddWithValue("scope", scope);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertCursorAsync(
      NpgsqlConnection connection, Guid streamId, string perspectiveName, Guid? lastEventId,
      short status, Guid? rewindTriggerEventId = null) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_cursors
        (stream_id, perspective_name, last_event_id, status, processed_at, rewind_trigger_event_id)
      VALUES (@stream, @name, @last_event, @status, NOW(), @trigger)";
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("name", perspectiveName);
    ins.Parameters.AddWithValue("last_event", (object?)lastEventId ?? DBNull.Value);
    ins.Parameters.AddWithValue("status", status);
    ins.Parameters.AddWithValue("trigger", (object?)rewindTriggerEventId ?? DBNull.Value);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertLifecycleCompletionAsync(
      NpgsqlConnection connection, Guid eventId, DateTimeOffset completedAt) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_lifecycle_completions (event_id, instance_id, completed_at)
      VALUES (@evt, @inst, @completed)";
    ins.Parameters.AddWithValue("evt", eventId);
    ins.Parameters.AddWithValue("inst", Guid.NewGuid());
    ins.Parameters.Add(new NpgsqlParameter("completed", NpgsqlDbType.TimestampTz) { Value = completedAt });
    await ins.ExecuteNonQueryAsync();
  }
}
