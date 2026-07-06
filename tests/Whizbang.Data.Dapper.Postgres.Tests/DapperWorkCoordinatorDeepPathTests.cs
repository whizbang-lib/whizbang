using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Deep-path companion to <see cref="DapperWorkCoordinatorBroadTests"/> and
/// <see cref="DapperWorkCoordinatorWithDataTests"/>. Covers the paths those suites
/// leave unwalked:
/// <list type="bullet">
///   <item><description><c>ClaimWorkAsync</c> row-classification arms — the
///     <c>perspective_stream</c> accumulation branch and the outbox/inbox
///     <see cref="NotImplementedException"/> guard (Phase C placeholder).</description></item>
///   <item><description><c>GetStreamEventsAsync</c> ownership-gate variants from migration 059 —
///     live-owner, dead-owner, unexpired foreign lease, and expired-lease reclaim.</description></item>
///   <item><description><c>CompletePerspectiveEventsAsync</c> with non-empty work ids in both
///     production (delete) and debug (retain + stamp) modes.</description></item>
///   <item><description><c>CompletePerspectiveAsync</c> with event work ids only (empty-cursors
///     "[]" serialization branch).</description></item>
/// </list>
/// </summary>
public class DapperWorkCoordinatorDeepPathTests : PostgresTestBase {

  private readonly JsonSerializerOptions _jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private DapperWorkCoordinator _build() {
    return new DapperWorkCoordinator(
      ConnectionString,
      _jsonOptions,
      NullLogger<DapperWorkCoordinator>.Instance);
  }

  private static OutboxMessage _makeOutbox(Guid msgId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(msgId),
      JsonDocument.Parse("{\"k\":1}").RootElement,
      []);
    return new OutboxMessage {
      MessageId = msgId,
      Destination = "deep-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Test.X, Test",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      StreamId = streamId,
      IsEvent = false,
    };
  }

  private static InboxMessage _makeInbox(Guid msgId, Guid streamId, bool isEvent) {
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(msgId),
      JsonDocument.Parse("{\"p\":1}").RootElement,
      []);
    return new InboxMessage {
      MessageId = msgId,
      HandlerName = "DeepHandler",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Test.X, Test",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      StreamId = streamId,
      IsEvent = isEvent,
    };
  }

  private static async Task _seedEventStoreRowAsync(
      NpgsqlConnection conn, Guid eventId, Guid streamId, int version, long? commitSequence) {
    await conn.ExecuteAsync(@"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type,
         event_data, metadata, scope, created_at, commit_sequence)
      VALUES (@id, @stream, @stream, 'TestAgg', @version, 'Test.OrderCreated, Test',
              '{""amount"": 42}'::jsonb, '{""Hops"": []}'::jsonb,
              '{""t"": ""tenant-7""}'::jsonb, NOW(), @cs)",
      new { id = eventId, stream = streamId, version, cs = commitSequence });
  }

  private static async Task _seedPerspectiveEventAsync(
      NpgsqlConnection conn, Guid workId, Guid streamId, string perspectiveName, Guid eventId,
      Guid? instanceId, DateTimeOffset? leaseExpiry, int attempts) {
    await conn.ExecuteAsync(@"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
         partition_number, status, attempts, created_at, claimed_at, processed_at)
      VALUES (@work, @stream, @persp, @event, @inst, @lease, 0, 0, @attempts, NOW(), NULL, NULL)",
      new { work = workId, stream = streamId, persp = perspectiveName, @event = eventId, inst = instanceId, lease = leaseExpiry, attempts });
  }

  private static async Task _registerLiveInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await conn.ExecuteAsync(@"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'other-svc', 'other-host', 2, NOW(), NOW(), '{}'::jsonb)",
      new { id = instanceId });
  }

  private static async Task _assignStreamOwnerAsync(NpgsqlConnection conn, Guid streamId, Guid ownerInstanceId) {
    await conn.ExecuteAsync(@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry)
      VALUES (@s, 0, @owner, NOW() + INTERVAL '5 minutes')",
      new { s = streamId, owner = ownerInstanceId });
  }

  // ----- ClaimWorkAsync row-classification arms -----

  [Test]
  public async Task ClaimWorkAsync_OwnedPerspectiveEvents_ReturnsPerspectiveStreamIdsAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    // claim_work requires a registered live caller (calculate_instance_rank raises otherwise).
    await c.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "svc-claim", "host-claim", 1));

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedEventStoreRowAsync(conn, eventId, streamId, version: 1, commitSequence: 10L);
    // Row already leased to the caller so claim_work's perspective projection returns
    // the stream without going through the orphan-claim path.
    await _seedPerspectiveEventAsync(
      conn, workId, streamId, "ClaimPerspective", eventId,
      instanceId, DateTimeOffset.UtcNow.AddMinutes(5), attempts: 0);

    var batch = await c.ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc-claim", "host-claim", 1,
      MaxStreams: 50, PartitionCount: 100, LeaseSeconds: 300));

    await Assert.That(batch.PerspectiveStreamIds.Count).IsEqualTo(1);
    await Assert.That(batch.PerspectiveStreamIds[0]).IsEqualTo(streamId);
    await Assert.That(batch.OutboxStreamIds.Count).IsEqualTo(0);
    await Assert.That(batch.InboxStreamIds.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimWorkAsync_OwnedOutboxWork_ThrowsNotImplementedAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    await c.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "svc-claim-o", "host-claim-o", 1));
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, streamId)], partitionCount: 100);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(
      "UPDATE wh_outbox SET instance_id = @i, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @m",
      new { i = instanceId, m = msgId });

    var threw = false;
    try {
      await c.ClaimWorkAsync(new ClaimWorkRequest(
        instanceId, "svc-claim-o", "host-claim-o", 1,
        MaxStreams: 50, PartitionCount: 100, LeaseSeconds: 300));
    } catch (NotImplementedException) { threw = true; }
    await Assert.That(threw).IsTrue()
      .Because("Dapper ClaimWorkAsync must refuse outbox rows until the Phase C envelope path lands.");
  }

  [Test]
  public async Task ClaimWorkAsync_OwnedInboxWork_ThrowsNotImplementedAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    await c.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "svc-claim-i", "host-claim-i", 1));
    // isEvent: false keeps claim_work's inbox emit-chain guard out of the picture —
    // the test targets only the C# 'inbox' source classification arm.
    await c.StoreInboxMessagesAsync([_makeInbox(msgId, streamId, isEvent: false)], partitionCount: 100);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(
      "UPDATE wh_inbox SET instance_id = @i, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @m",
      new { i = instanceId, m = msgId });

    var threw = false;
    try {
      await c.ClaimWorkAsync(new ClaimWorkRequest(
        instanceId, "svc-claim-i", "host-claim-i", 1,
        MaxStreams: 50, PartitionCount: 100, LeaseSeconds: 300));
    } catch (NotImplementedException) { threw = true; }
    await Assert.That(threw).IsTrue()
      .Because("Dapper ClaimWorkAsync must refuse inbox rows until the Phase C envelope path lands.");
  }

  // ----- GetStreamEventsAsync ownership-gate variants (migration 059) -----

  [Test]
  public async Task GetStreamEventsAsync_StreamOwnedByLiveInstance_LeavesRowUnclaimedAsync() {
    var c = _build();
    var callerId = (Guid)TrackedGuid.NewMedo();
    var ownerId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedEventStoreRowAsync(conn, eventId, streamId, version: 1, commitSequence: 100L);
    await _seedPerspectiveEventAsync(
      conn, workId, streamId, "OwnedPerspective", eventId,
      instanceId: null, leaseExpiry: null, attempts: 0);
    // Stream assigned to a DIFFERENT instance that is alive (wh_service_instances row exists):
    // the mig-059 single-writer gate must refuse the claim.
    await _registerLiveInstanceAsync(conn, ownerId);
    await _assignStreamOwnerAsync(conn, streamId, ownerId);

    var events = await c.GetStreamEventsAsync(callerId, [streamId]);

    await Assert.That(events.Count).IsEqualTo(0)
      .Because("A stream owned by a different live instance must not be claimable (single-writer gate).");
    var claimedBy = await conn.ExecuteScalarAsync<Guid?>(
      "SELECT instance_id FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(claimedBy).IsNull();
  }

  [Test]
  public async Task GetStreamEventsAsync_StreamOwnedByDeadInstance_ClaimsAndReturnsRowAsync() {
    var c = _build();
    var callerId = (Guid)TrackedGuid.NewMedo();
    var deadOwnerId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedEventStoreRowAsync(conn, eventId, streamId, version: 1, commitSequence: 101L);
    await _seedPerspectiveEventAsync(
      conn, workId, streamId, "DeadOwnerPerspective", eventId,
      instanceId: null, leaseExpiry: null, attempts: 0);
    // Stream assigned to an instance with NO wh_service_instances row and no live LISTEN
    // connection — a dead owner. Failover must remain possible.
    await _assignStreamOwnerAsync(conn, streamId, deadOwnerId);

    var events = await c.GetStreamEventsAsync(callerId, [streamId]);

    await Assert.That(events.Count).IsEqualTo(1)
      .Because("Streams whose assigned owner is dead stay claimable for clean failover.");
    await Assert.That(events[0].EventWorkId).IsEqualTo(workId);
    var claimedBy = await conn.ExecuteScalarAsync<Guid?>(
      "SELECT instance_id FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(claimedBy).IsEqualTo(callerId);
  }

  [Test]
  public async Task GetStreamEventsAsync_RowLeasedToOtherInstanceUnexpired_ReturnsEmptyAsync() {
    var c = _build();
    var callerId = (Guid)TrackedGuid.NewMedo();
    var holderId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedEventStoreRowAsync(conn, eventId, streamId, version: 1, commitSequence: 102L);
    // Row-level lease held by another instance and NOT yet expired: neither claimable nor returned.
    await _seedPerspectiveEventAsync(
      conn, workId, streamId, "HeldPerspective", eventId,
      holderId, DateTimeOffset.UtcNow.AddMinutes(5), attempts: 1);

    var events = await c.GetStreamEventsAsync(callerId, [streamId]);

    await Assert.That(events.Count).IsEqualTo(0);
    var claimedBy = await conn.ExecuteScalarAsync<Guid?>(
      "SELECT instance_id FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(claimedBy).IsEqualTo(holderId)
      .Because("An unexpired foreign lease must survive another instance's fetch attempt.");
  }

  [Test]
  public async Task GetStreamEventsAsync_ExpiredLeaseOnUnownedStream_ReclaimsAndBumpsAttemptsAsync() {
    var c = _build();
    var callerId = (Guid)TrackedGuid.NewMedo();
    var previousHolderId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedEventStoreRowAsync(conn, eventId, streamId, version: 1, commitSequence: 103L);
    // Lease expired a minute ago and no wh_active_streams owner: the caller may reclaim.
    await _seedPerspectiveEventAsync(
      conn, workId, streamId, "ExpiredPerspective", eventId,
      previousHolderId, DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 1);

    var events = await c.GetStreamEventsAsync(callerId, [streamId]);

    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0].EventWorkId).IsEqualTo(workId);
    await Assert.That(events[0].Attempts).IsEqualTo(2)
      .Because("Reclaiming an expired-lease row bumps attempts 1 -> 2.");
    var claimedBy = await conn.ExecuteScalarAsync<Guid?>(
      "SELECT instance_id FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(claimedBy).IsEqualTo(callerId);
  }

  // ----- CompletePerspectiveEventsAsync with non-empty work ids -----

  [Test]
  public async Task CompletePerspectiveEventsAsync_ProductionMode_DeletesRowsAsync() {
    var c = _build();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventA = (Guid)TrackedGuid.NewMedo();
    var eventB = (Guid)TrackedGuid.NewMedo();
    var workA = (Guid)TrackedGuid.NewMedo();
    var workB = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedEventStoreRowAsync(conn, eventA, streamId, version: 1, commitSequence: 1L);
    await _seedEventStoreRowAsync(conn, eventB, streamId, version: 2, commitSequence: 2L);
    await _seedPerspectiveEventAsync(conn, workA, streamId, "DonePerspective", eventA, instanceId: null, leaseExpiry: null, attempts: 0);
    await _seedPerspectiveEventAsync(conn, workB, streamId, "DonePerspective", eventB, instanceId: null, leaseExpiry: null, attempts: 0);

    var affected = await c.CompletePerspectiveEventsAsync([workA, workB], debugMode: false);

    await Assert.That(affected).IsEqualTo(2);
    var remaining = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_perspective_events WHERE event_work_id = ANY(@ids)",
      new { ids = new[] { workA, workB } });
    await Assert.That(remaining).IsEqualTo(0L)
      .Because("Production-mode completion deletes the work rows outright.");
  }

  [Test]
  public async Task CompletePerspectiveEventsAsync_DebugMode_RetainsRowsStampedProcessedAsync() {
    var c = _build();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedEventStoreRowAsync(conn, eventId, streamId, version: 1, commitSequence: 3L);
    await _seedPerspectiveEventAsync(
      conn, workId, streamId, "DebugPerspective", eventId,
      (Guid)TrackedGuid.NewMedo(), DateTimeOffset.UtcNow.AddMinutes(5), attempts: 1);

    var affected = await c.CompletePerspectiveEventsAsync([workId], debugMode: true);

    await Assert.That(affected).IsEqualTo(1);
    var processedAt = await conn.ExecuteScalarAsync<DateTime?>(
      "SELECT processed_at FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(processedAt).IsNotNull()
      .Because("Debug mode keeps the forensic row with a kept-marker timestamp.");
    var instanceAfter = await conn.ExecuteScalarAsync<Guid?>(
      "SELECT instance_id FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(instanceAfter).IsNull()
      .Because("Debug-mode completion releases the lease on the retained row.");
  }

  // ----- CompletePerspectiveAsync: event work ids only (empty-cursors branch) -----

  [Test]
  public async Task CompletePerspectiveAsync_EventWorkIdsOnly_DeletesWorkRowsAsync() {
    var c = _build();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedEventStoreRowAsync(conn, eventId, streamId, version: 1, commitSequence: 4L);
    await _seedPerspectiveEventAsync(conn, workId, streamId, "IdsOnlyPerspective", eventId, instanceId: null, leaseExpiry: null, attempts: 0);

    // cursors empty + ids non-empty exercises the '[]' cursorsJson short-circuit branch.
    await c.CompletePerspectiveAsync([], eventWorkIds: [workId], debugMode: false);

    var remaining = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(remaining).IsEqualTo(0L);
  }
}
