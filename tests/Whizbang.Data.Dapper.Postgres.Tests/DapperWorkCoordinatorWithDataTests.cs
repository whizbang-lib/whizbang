using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Data-driven companion to <see cref="DapperWorkCoordinatorBroadTests"/>. Where the broad
/// suite walks empty/happy paths, these tests seed real rows (via raw SQL or the coordinator's
/// own store methods) and assert field-by-field row mapping, claim side-effects in the
/// database, failure persistence, and JSON round-trip fidelity of the serialize helpers
/// (<c>_serializeNewOutboxMessages</c>, <c>_serializeNewInboxMessages</c>,
/// <c>_serializeFailures</c>, <c>_serializePerspectiveCompletions</c>) with non-empty inputs.
/// </summary>
public class DapperWorkCoordinatorWithDataTests : PostgresTestBase {

  // Production wires the coordinator with app-level options that chain the framework
  // context (MessageId/TrackedGuid converters etc.) — bare InfrastructureJsonContext
  // serializes MessageId as {} and breaks envelope round-trips.
  private readonly JsonSerializerOptions _jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private DapperWorkCoordinator _build() {
    return new DapperWorkCoordinator(
      ConnectionString,
      _jsonOptions,
      NullLogger<DapperWorkCoordinator>.Instance);
  }

  private static ServiceInstanceInfo _makeServiceInstance() {
    return new ServiceInstanceInfo {
      ServiceName = "test-svc",
      InstanceId = (Guid)TrackedGuid.NewMedo(),
      HostName = "test-host",
      ProcessId = 4242,
    };
  }

  /// <summary>
  /// Two hops with every optional field the MessageHopConverter round-trips populated on
  /// the first hop, so serialization tests exercise the non-null branches.
  /// </summary>
  private static List<MessageHop> _makeHops(Guid streamId) {
    return [
      new MessageHop {
        ServiceInstance = _makeServiceInstance(),
        Topic = "hop-topic-1",
        StreamId = streamId.ToString(),
        CausationType = "Test.ParentCmd, Test",
        PartitionIndex = 3,
        SequenceNumber = 42,
        ExecutionStrategy = "SerialExecutor",
        Metadata = new Dictionary<string, JsonElement> {
          ["k1"] = JsonDocument.Parse("\"v1\"").RootElement,
        },
      },
      new MessageHop {
        ServiceInstance = _makeServiceInstance(),
        Topic = "hop-topic-2",
      },
    ];
  }

  private static PerspectiveScope _makeScope() {
    return new PerspectiveScope {
      TenantId = "tenant-42",
      UserId = "user-9",
      AllowedPrincipals = ["user:alice", "group:ops"],
    };
  }

  private static OutboxMessage _makeOutbox(
      Guid msgId, Guid streamId, List<MessageHop> hops, bool isEvent = false, string? destination = "orders-topic") {
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(msgId),
      JsonDocument.Parse("{\"k\":1}").RootElement,
      hops);
    return new OutboxMessage {
      MessageId = msgId,
      Destination = destination,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Test.X, Test",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = hops },
      Scope = _makeScope(),
      StreamId = streamId,
      IsEvent = isEvent,
    };
  }

  private static InboxMessage _makeInbox(
      Guid msgId, Guid streamId, List<MessageHop> hops, string messageType = "Test.X, Test") {
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(msgId),
      JsonDocument.Parse("{\"p\":1}").RootElement,
      hops);
    return new InboxMessage {
      MessageId = msgId,
      HandlerName = "TestHandler",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = messageType,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = hops },
      Scope = _makeScope(),
      StreamId = streamId,
      IsEvent = true,
    };
  }

  private static async Task _seedEventStoreRowAsync(
      NpgsqlConnection conn, Guid eventId, Guid streamId, int version, long? commitSequence) {
    await conn.ExecuteAsync(@"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type,
         event_data, metadata, scope, created_at, commit_sequence)
      VALUES (@id, @stream, @stream, 'TestAgg', @version, 'Test.OrderCreated, Test',
              '{""amount"": 42}'::jsonb, '{""Hops"": [{""to"": ""seeded""}]}'::jsonb,
              '{""t"": ""tenant-7""}'::jsonb, NOW(), @cs)",
      new { id = eventId, stream = streamId, version, cs = commitSequence });
  }

  private static async Task _seedUnclaimedPerspectiveEventAsync(
      NpgsqlConnection conn, Guid workId, Guid streamId, string perspectiveName, Guid eventId) {
    await conn.ExecuteAsync(@"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
         partition_number, status, attempts, created_at, claimed_at, processed_at)
      VALUES (@work, @stream, @persp, @event, NULL, NULL, 0, 0, 0, NOW(), NULL, NULL)",
      new { work = workId, stream = streamId, persp = perspectiveName, @event = eventId });
  }

  // ----- GetStreamEventsAsync row mapping -----

  [Test]
  public async Task GetStreamEventsAsync_SeededClaimableEvent_MapsAllFieldsAndClaimsRowAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedEventStoreRowAsync(conn, eventId, streamId, version: 1, commitSequence: 100L);
    await _seedUnclaimedPerspectiveEventAsync(conn, workId, streamId, "MapPerspective", eventId);

    var events = await c.GetStreamEventsAsync(instanceId, [streamId]);

    await Assert.That(events.Count).IsEqualTo(1);
    var row = events[0];
    await Assert.That(row.StreamId).IsEqualTo(streamId);
    await Assert.That(row.EventId).IsEqualTo(eventId);
    await Assert.That(row.EventType).IsEqualTo("Test.OrderCreated, Test");
    await Assert.That(row.EventWorkId).IsEqualTo(workId);
    await Assert.That(row.Attempts).IsEqualTo(1)
      .Because("get_stream_events atomically claims the row, bumping attempts 0 -> 1.");

    using var eventData = JsonDocument.Parse(row.EventData);
    await Assert.That(eventData.RootElement.GetProperty("amount").GetInt32()).IsEqualTo(42);
    await Assert.That(row.Metadata).IsNotNull();
    using var metadata = JsonDocument.Parse(row.Metadata!);
    await Assert.That(metadata.RootElement.GetProperty("Hops")[0].GetProperty("to").GetString())
      .IsEqualTo("seeded");
    await Assert.That(row.Scope).IsNotNull();
    using var scope = JsonDocument.Parse(row.Scope!);
    await Assert.That(scope.RootElement.GetProperty("t").GetString()).IsEqualTo("tenant-7");

    // Claim side-effect must be visible in the database: row now leased to the caller.
    var claimedBy = await conn.ExecuteScalarAsync<Guid?>(
      "SELECT instance_id FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(claimedBy).IsEqualTo(instanceId);
    var leaseExpiry = await conn.ExecuteScalarAsync<DateTime?>(
      "SELECT lease_expiry FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(leaseExpiry).IsNotNull();
  }

  // ----- ClaimAndFetchPendingPerspectiveEventsAsync -----

  [Test]
  public async Task ClaimAndFetchPendingPerspectiveEventsAsync_UnclaimedStampedRows_ClaimsAndReturnsOrderedRowsAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "DrainPerspective";
    var eventA = (Guid)TrackedGuid.NewMedo();
    var eventB = (Guid)TrackedGuid.NewMedo();
    var workA = (Guid)TrackedGuid.NewMedo();
    var workB = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedEventStoreRowAsync(conn, eventA, streamId, version: 1, commitSequence: 10L);
    await _seedEventStoreRowAsync(conn, eventB, streamId, version: 2, commitSequence: 20L);
    await _seedUnclaimedPerspectiveEventAsync(conn, workA, streamId, perspectiveName, eventA);
    await _seedUnclaimedPerspectiveEventAsync(conn, workB, streamId, perspectiveName, eventB);

    var rows = await c.ClaimAndFetchPendingPerspectiveEventsAsync(
      streamId, perspectiveName, instanceId, TimeSpan.FromMinutes(5));

    await Assert.That(rows.Count).IsEqualTo(2);
    var byEvent = rows.ToDictionary(r => r.EventId);
    await Assert.That(byEvent[eventA].EventWorkId).IsEqualTo(workA);
    await Assert.That(byEvent[eventA].CommitSequence).IsEqualTo(10L);
    await Assert.That(byEvent[eventB].EventWorkId).IsEqualTo(workB);
    await Assert.That(byEvent[eventB].CommitSequence).IsEqualTo(20L);

    // SQL orders by event_id ASC (PostgreSQL uuid byte order == canonical string order).
    var returnedOrder = rows.Select(r => r.EventId.ToString()).ToList();
    var expectedOrder = returnedOrder.OrderBy(s => s, StringComparer.Ordinal).ToList();
    await Assert.That(returnedOrder[0]).IsEqualTo(expectedOrder[0]);
    await Assert.That(returnedOrder[1]).IsEqualTo(expectedOrder[1]);

    // Claim side-effect: both rows leased to the caller with a future lease and attempts bumped.
    var claimedCount = await conn.ExecuteScalarAsync<long>(@"
      SELECT COUNT(*) FROM wh_perspective_events
      WHERE stream_id = @s AND perspective_name = @p
        AND instance_id = @i AND lease_expiry > NOW() AND attempts = 1",
      new { s = streamId, p = perspectiveName, i = instanceId });
    await Assert.That(claimedCount).IsEqualTo(2L);
  }

  [Test]
  public async Task ClaimAndFetchPendingPerspectiveEventsAsync_UnstampedRow_ReturnsEmptyAndLeavesRowUnclaimedAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "UnstampedPerspective";
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // commit_sequence NULL: stamper hasn't caught up, row must be invisible to the claim.
    await _seedEventStoreRowAsync(conn, eventId, streamId, version: 1, commitSequence: null);
    await _seedUnclaimedPerspectiveEventAsync(conn, workId, streamId, perspectiveName, eventId);

    var rows = await c.ClaimAndFetchPendingPerspectiveEventsAsync(
      streamId, perspectiveName, instanceId, TimeSpan.FromMinutes(5));

    await Assert.That(rows.Count).IsEqualTo(0)
      .Because("Rows without an authoritative commit_sequence must be neither leased nor returned.");

    var claimedBy = await conn.ExecuteScalarAsync<Guid?>(
      "SELECT instance_id FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(claimedBy).IsNull();
    var attempts = await conn.ExecuteScalarAsync<int>(
      "SELECT attempts FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(attempts).IsEqualTo(0);
  }

  // ----- FetchOutboxBatchAsync / FetchInboxBatchAsync full-row mapping -----

  [Test]
  public async Task FetchOutboxBatchAsync_PopulatedRows_MapsAllColumnsAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgA = (Guid)TrackedGuid.NewMedo();
    var msgB = (Guid)TrackedGuid.NewMedo();
    var hops = _makeHops(streamId);

    await c.StoreOutboxMessagesAsync(
      [_makeOutbox(msgA, streamId, hops), _makeOutbox(msgB, streamId, hops, isEvent: true)],
      partitionCount: 100);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(
      "UPDATE wh_outbox SET instance_id = @i, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
      new { i = instanceId, ids = new[] { msgA, msgB } });
    await conn.ExecuteAsync(
      "UPDATE wh_outbox SET error = 'previous publish failure' WHERE message_id = @m",
      new { m = msgA });

    var rows = await c.FetchOutboxBatchAsync([streamId], instanceId, maxPerStream: 10);

    await Assert.That(rows.Count).IsEqualTo(2);
    var rowA = rows.Single(r => r.MessageId == msgA);
    await Assert.That(rowA.StreamId).IsEqualTo(streamId);
    await Assert.That(rowA.Destination).IsEqualTo("orders-topic");
    await Assert.That(rowA.MessageType).IsEqualTo("Test.X, Test");
    await Assert.That(rowA.EnvelopeType)
      .IsEqualTo("Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core");
    await Assert.That(rowA.Status).IsEqualTo(1)
      .Because("store_outbox_messages inserts rows with the Stored flag (1).");
    await Assert.That(rowA.Attempts).IsEqualTo(0);
    await Assert.That(rowA.PartitionNumber).IsNotNull();
    await Assert.That(rowA.IsEvent).IsFalse();
    await Assert.That(rowA.Error).IsEqualTo("previous publish failure");

    using var envelopeJson = JsonDocument.Parse(rowA.EventData);
    await Assert.That(envelopeJson.RootElement.GetProperty("p").GetProperty("k").GetInt32()).IsEqualTo(1);
    await Assert.That(envelopeJson.RootElement.GetProperty("h").GetArrayLength()).IsEqualTo(2);
    using var metadataJson = JsonDocument.Parse(rowA.Metadata);
    await Assert.That(metadataJson.RootElement.GetProperty("Hops").GetArrayLength()).IsEqualTo(2);
    await Assert.That(rowA.Scope).IsNotNull();
    using var scopeJson = JsonDocument.Parse(rowA.Scope!);
    await Assert.That(scopeJson.RootElement.GetProperty("t").GetString()).IsEqualTo("tenant-42");
    await Assert.That(scopeJson.RootElement.GetProperty("u").GetString()).IsEqualTo("user-9");
    await Assert.That(scopeJson.RootElement.GetProperty("ap")[0].GetString()).IsEqualTo("user:alice");

    var rowB = rows.Single(r => r.MessageId == msgB);
    await Assert.That(rowB.IsEvent).IsTrue();
    await Assert.That(rowB.Error).IsNull();
  }

  [Test]
  public async Task FetchInboxBatchAsync_PopulatedRow_MapsAllColumnsAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var hops = _makeHops(streamId);

    await c.StoreInboxMessagesAsync([_makeInbox(msgId, streamId, hops)], partitionCount: 100);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(@"
      UPDATE wh_inbox
      SET instance_id = @i, lease_expiry = NOW() + INTERVAL '5 minutes', error = 'handler blew up'
      WHERE message_id = @m",
      new { i = instanceId, m = msgId });

    var rows = await c.FetchInboxBatchAsync([streamId], instanceId, maxPerStream: 10);

    await Assert.That(rows.Count).IsEqualTo(1);
    var row = rows[0];
    await Assert.That(row.MessageId).IsEqualTo(msgId);
    await Assert.That(row.StreamId).IsEqualTo(streamId);
    await Assert.That(row.HandlerName).IsEqualTo("TestHandler");
    await Assert.That(row.MessageType).IsEqualTo("Test.X, Test");
    await Assert.That(row.Status).IsEqualTo(1);
    await Assert.That(row.Attempts).IsEqualTo(0);
    await Assert.That(row.PartitionNumber).IsNotNull();
    await Assert.That(row.IsEvent).IsTrue();
    await Assert.That(row.Error).IsEqualTo("handler blew up");

    using var envelopeJson = JsonDocument.Parse(row.EventData);
    await Assert.That(envelopeJson.RootElement.GetProperty("p").GetProperty("p").GetInt32()).IsEqualTo(1);
    await Assert.That(envelopeJson.RootElement.GetProperty("h").GetArrayLength()).IsEqualTo(2);
    using var metadataJson = JsonDocument.Parse(row.Metadata);
    await Assert.That(metadataJson.RootElement.GetProperty("Hops").GetArrayLength()).IsEqualTo(2);
    await Assert.That(row.Scope).IsNotNull();
    using var scopeJson = JsonDocument.Parse(row.Scope!);
    await Assert.That(scopeJson.RootElement.GetProperty("t").GetString()).IsEqualTo("tenant-42");
  }

  // ----- ReportPerspectiveFailureAsync / ReportPerspectiveCompletionAsync persistence -----

  [Test]
  public async Task ReportPerspectiveFailureAsync_ExistingCursor_PersistsFailureAndMarksProcessedEventsAsync() {
    var c = _build();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "FailingPerspective";
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(@"
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status)
      VALUES (@s, @p, NULL, 1)",
      new { s = streamId, p = perspectiveName });
    await _seedEventStoreRowAsync(conn, eventId, streamId, version: 1, commitSequence: 5L);
    await _seedUnclaimedPerspectiveEventAsync(conn, workId, streamId, perspectiveName, eventId);

    await c.ReportPerspectiveFailureAsync(new PerspectiveCursorFailure {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = eventId,
      ProcessedEventIds = [eventId],
      Status = PerspectiveProcessingStatus.Failed,
      Error = "projection blew up",
    });

    var error = await conn.ExecuteScalarAsync<string?>(
      "SELECT error FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p",
      new { s = streamId, p = perspectiveName });
    await Assert.That(error).IsEqualTo("projection blew up");
    var status = await conn.ExecuteScalarAsync<short>(
      "SELECT status FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p",
      new { s = streamId, p = perspectiveName });
    await Assert.That((int)status).IsEqualTo((int)PerspectiveProcessingStatus.Failed);
    var lastEventId = await conn.ExecuteScalarAsync<Guid?>(
      "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p",
      new { s = streamId, p = perspectiveName });
    await Assert.That(lastEventId).IsEqualTo(eventId);
    var cursorProcessedAt = await conn.ExecuteScalarAsync<DateTime?>(
      "SELECT processed_at FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p",
      new { s = streamId, p = perspectiveName });
    await Assert.That(cursorProcessedAt).IsNotNull();

    // ProcessedEventIds must be marked processed on wh_perspective_events.
    var eventProcessedAt = await conn.ExecuteScalarAsync<DateTime?>(
      "SELECT processed_at FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(eventProcessedAt).IsNotNull();
  }

  [Test]
  public async Task ReportPerspectiveCompletionAsync_ExistingCursor_AdvancesCursorAndClearsErrorAsync() {
    var c = _build();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "CompletingPerspective";
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(@"
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, error)
      VALUES (@s, @p, NULL, 4, 'old error')",
      new { s = streamId, p = perspectiveName });
    await _seedEventStoreRowAsync(conn, eventId, streamId, version: 1, commitSequence: 6L);
    await _seedUnclaimedPerspectiveEventAsync(conn, workId, streamId, perspectiveName, eventId);

    await c.ReportPerspectiveCompletionAsync(new PerspectiveCursorCompletion {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = eventId,
      ProcessedEventIds = [eventId],
      Status = PerspectiveProcessingStatus.Completed,
    });

    var error = await conn.ExecuteScalarAsync<string?>(
      "SELECT error FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p",
      new { s = streamId, p = perspectiveName });
    await Assert.That(error).IsNull()
      .Because("A successful completion must clear the previous failure's error text.");
    var status = await conn.ExecuteScalarAsync<short>(
      "SELECT status FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p",
      new { s = streamId, p = perspectiveName });
    await Assert.That((int)status).IsEqualTo((int)PerspectiveProcessingStatus.Completed);
    var lastEventId = await conn.ExecuteScalarAsync<Guid?>(
      "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p",
      new { s = streamId, p = perspectiveName });
    await Assert.That(lastEventId).IsEqualTo(eventId);
    var eventProcessedAt = await conn.ExecuteScalarAsync<DateTime?>(
      "SELECT processed_at FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(eventProcessedAt).IsNotNull();
  }

  // ----- Serialize helpers via public API: non-empty hops / scope / metadata round trip -----

  [Test]
  public async Task StoreOutboxMessagesAsync_RichEnvelope_RoundTripsHopsScopeAndMetadataAsync() {
    var c = _build();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var hops = _makeHops(streamId);

    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, streamId, hops)], partitionCount: 100);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var eventDataJson = await conn.ExecuteScalarAsync<string>(
      "SELECT event_data::text FROM wh_outbox WHERE message_id = @m", new { m = msgId });
    var metadataJson = await conn.ExecuteScalarAsync<string>(
      "SELECT metadata::text FROM wh_outbox WHERE message_id = @m", new { m = msgId });
    var scopeJson = await conn.ExecuteScalarAsync<string>(
      "SELECT scope::text FROM wh_outbox WHERE message_id = @m", new { m = msgId });

    // Envelope round trip through the same JsonSerializerOptions the coordinator serialized with.
    var envelopeTypeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var envelope = (MessageEnvelope<JsonElement>?)JsonSerializer.Deserialize(eventDataJson!, envelopeTypeInfo);
    await Assert.That(envelope).IsNotNull();
    var env = envelope!;
    await Assert.That(env.MessageId).IsEqualTo(MessageId.From(msgId));
    await Assert.That(env.Payload.GetProperty("k").GetInt32()).IsEqualTo(1);
    var envHops = env.Hops;
    await Assert.That(envHops.Count).IsEqualTo(2);

    var hop0 = envHops[0];
    await Assert.That(hop0.Topic).IsEqualTo("hop-topic-1");
    await Assert.That(hop0.StreamId).IsEqualTo(streamId.ToString());
    await Assert.That(hop0.CausationType).IsEqualTo("Test.ParentCmd, Test");
    await Assert.That(hop0.PartitionIndex).IsEqualTo(3);
    await Assert.That(hop0.SequenceNumber).IsEqualTo(42L);
    await Assert.That(hop0.ExecutionStrategy).IsEqualTo("SerialExecutor");
    await Assert.That(hop0.Metadata).IsNotNull();
    await Assert.That(hop0.Metadata!["k1"].GetString()).IsEqualTo("v1");
    await Assert.That(hop0.ServiceInstance.ServiceName).IsEqualTo("test-svc");
    await Assert.That(hop0.ServiceInstance.HostName).IsEqualTo("test-host");
    await Assert.That(hop0.ServiceInstance.ProcessId).IsEqualTo(4242);
    await Assert.That(envHops[1].Topic).IsEqualTo("hop-topic-2");

    // Metadata column carries EnvelopeMetadata (long-name Hops with short-name hop fields).
    using var metadataDoc = JsonDocument.Parse(metadataJson!);
    var metadataHops = metadataDoc.RootElement.GetProperty("Hops");
    await Assert.That(metadataHops.GetArrayLength()).IsEqualTo(2);
    await Assert.That(metadataHops[0].GetProperty("to").GetString()).IsEqualTo("hop-topic-1");

    // Scope column carries the PerspectiveScope short-name JSON.
    using var scopeDoc = JsonDocument.Parse(scopeJson!);
    await Assert.That(scopeDoc.RootElement.GetProperty("t").GetString()).IsEqualTo("tenant-42");
    await Assert.That(scopeDoc.RootElement.GetProperty("u").GetString()).IsEqualTo("user-9");
    await Assert.That(scopeDoc.RootElement.GetProperty("ap").GetArrayLength()).IsEqualTo(2);
    await Assert.That(scopeDoc.RootElement.GetProperty("ap")[1].GetString()).IsEqualTo("group:ops");
  }

  // ----- ReportFailuresAsync with populated failures -----

  [Test]
  public async Task ReportFailuresAsync_OutboxFailure_PersistsErrorAndReleasesLeaseAsync() {
    var c = _build();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, streamId, _makeHops(streamId))], partitionCount: 100);

    await c.ReportFailuresAsync(WorkCategory.Outbox, [
      new MessageFailure {
        MessageId = msgId,
        CompletedStatus = MessageProcessingStatus.Stored,
        Error = "publish exploded",
      },
    ]);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var error = await conn.ExecuteScalarAsync<string?>(
      "SELECT error FROM wh_outbox WHERE message_id = @m", new { m = msgId });
    await Assert.That(error).IsEqualTo("publish exploded");
    var status = await conn.ExecuteScalarAsync<int>(
      "SELECT status FROM wh_outbox WHERE message_id = @m", new { m = msgId });
    await Assert.That(status & 32768).IsEqualTo(32768)
      .Because("process_outbox_failures must OR in the Failed bit (32768).");
    var scheduledFor = await conn.ExecuteScalarAsync<DateTime?>(
      "SELECT scheduled_for FROM wh_outbox WHERE message_id = @m", new { m = msgId });
    await Assert.That(scheduledFor).IsNotNull()
      .Because("Failures schedule an exponential-backoff retry.");
    var leasedBy = await conn.ExecuteScalarAsync<Guid?>(
      "SELECT instance_id FROM wh_outbox WHERE message_id = @m", new { m = msgId });
    await Assert.That(leasedBy).IsNull()
      .Because("Failures release the lease so another instance can reclaim the row.");
  }

  // ----- FlushCompletionsAsync composite request -----

  [Test]
  public async Task FlushCompletionsAsync_CompletionsCursorsAndFailures_AppliesAllCategoriesAsync() {
    var c = _build();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgCompleted = (Guid)TrackedGuid.NewMedo();
    var msgFailed = (Guid)TrackedGuid.NewMedo();
    var hops = _makeHops(streamId);
    const string perspectiveName = "FlushPerspective";
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await c.StoreOutboxMessagesAsync(
      [_makeOutbox(msgCompleted, streamId, hops), _makeOutbox(msgFailed, streamId, hops)],
      partitionCount: 100);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedEventStoreRowAsync(conn, eventId, streamId, version: 1, commitSequence: 7L);
    await _seedUnclaimedPerspectiveEventAsync(conn, workId, streamId, perspectiveName, eventId);
    // A live system already has a cursor row for the pair (created by the work-batch
    // pipeline). update_perspective_cursors' INSERT path requires surviving processed
    // rows, which the flush's own completion delete removes first — so the UPDATE path
    // (status -> Completed on drain) is the one production exercises here.
    await conn.ExecuteAsync(
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES (@s, @p, @e, 0)",
      new { s = streamId, p = perspectiveName, e = eventId });

    await c.FlushCompletionsAsync(new FlushCompletionsRequest(
      OutboxIds: [msgCompleted],
      PerspectiveCursors: [
        new PerspectiveCursorCompletion {
          StreamId = streamId,
          PerspectiveName = perspectiveName,
          LastEventId = eventId,
          ProcessedEventIds = [eventId],
          Status = PerspectiveProcessingStatus.Completed,
        },
      ],
      PerspectiveEventWorkIds: [workId],
      FailuresByCategory: [
        new CategoryFailures(WorkCategory.Outbox, [
          new MessageFailure {
            MessageId = msgFailed,
            CompletedStatus = MessageProcessingStatus.Stored,
            Error = "flush failure",
          },
        ]),
      ]));

    var completedCount = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_outbox WHERE message_id = @m", new { m = msgCompleted });
    await Assert.That(completedCount).IsEqualTo(0L)
      .Because("Production-mode outbox completion deletes the published row.");
    var failedError = await conn.ExecuteScalarAsync<string?>(
      "SELECT error FROM wh_outbox WHERE message_id = @m", new { m = msgFailed });
    await Assert.That(failedError).IsEqualTo("flush failure");
    var eventWorkCount = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_perspective_events WHERE event_work_id = @w", new { w = workId });
    await Assert.That(eventWorkCount).IsEqualTo(0L)
      .Because("Production-mode perspective-event completion deletes the work row.");
    var cursorStatus = await conn.ExecuteScalarAsync<int>(
      "SELECT status FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p",
      new { s = streamId, p = perspectiveName });
    await Assert.That(cursorStatus).IsEqualTo(2)
      .Because("update_perspective_cursors marks the drained pair's cursor Completed (status=2).");
  }

  // ----- CommitHandlerResultAsync / CommitHandlerBatchAsync -----

  [Test]
  public async Task CommitHandlerResultAsync_CompletionWithEmittedMessages_AppliesAtomicBundleAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var handledStream = (Guid)TrackedGuid.NewMedo();
    var handledInboxId = (Guid)TrackedGuid.NewMedo();
    var emittedOutboxId = (Guid)TrackedGuid.NewMedo();
    var emittedOutboxStream = (Guid)TrackedGuid.NewMedo();
    var emittedInboxId = (Guid)TrackedGuid.NewMedo();
    var emittedInboxStream = (Guid)TrackedGuid.NewMedo();

    await c.StoreInboxMessagesAsync(
      [_makeInbox(handledInboxId, handledStream, _makeHops(handledStream))], partitionCount: 100);

    await c.CommitHandlerResultAsync(new HandlerCommitRequest(
      HandlerId: (Guid)TrackedGuid.NewMedo(),
      InstanceId: instanceId,
      ServiceName: "svc-h",
      HostName: "host-h",
      ProcessId: 7,
      PartitionCount: 100,
      InboxCompletion: new HandlerInboxCompletion(handledInboxId, Status: 2),
      NewOutboxMessages: [_makeOutbox(emittedOutboxId, emittedOutboxStream, _makeHops(emittedOutboxStream))],
      NewInboxMessages: [_makeInbox(emittedInboxId, emittedInboxStream, _makeHops(emittedInboxStream))]));

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var handledCount = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_inbox WHERE message_id = @m", new { m = handledInboxId });
    await Assert.That(handledCount).IsEqualTo(0L)
      .Because("EventStored completion in production mode deletes the acknowledged inbox row.");
    var emittedOutboxLease = await conn.ExecuteScalarAsync<Guid?>(
      "SELECT instance_id FROM wh_outbox WHERE message_id = @m", new { m = emittedOutboxId });
    await Assert.That(emittedOutboxLease).IsEqualTo(instanceId)
      .Because("commit_handler_result stores emitted outbox messages with an immediate lease.");
    var emittedInboxCount = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_inbox WHERE message_id = @m", new { m = emittedInboxId });
    await Assert.That(emittedInboxCount).IsEqualTo(1L);
  }

  [Test]
  public async Task CommitHandlerBatchAsync_TwoRequests_ReturnsSuccessPerHandlerAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    var inboxA = (Guid)TrackedGuid.NewMedo();
    var inboxB = (Guid)TrackedGuid.NewMedo();
    var handlerA = (Guid)TrackedGuid.NewMedo();
    var handlerB = (Guid)TrackedGuid.NewMedo();

    await c.StoreInboxMessagesAsync(
      [_makeInbox(inboxA, streamA, _makeHops(streamA)), _makeInbox(inboxB, streamB, _makeHops(streamB))],
      partitionCount: 100);

    HandlerCommitRequest makeRequest(Guid handlerId, Guid inboxId) => new(
      HandlerId: handlerId,
      InstanceId: instanceId,
      ServiceName: "svc-batch",
      HostName: "host-batch",
      ProcessId: 9,
      PartitionCount: 100,
      InboxCompletion: new HandlerInboxCompletion(inboxId, Status: 2));

    var results = await c.CommitHandlerBatchAsync([makeRequest(handlerA, inboxA), makeRequest(handlerB, inboxB)]);

    await Assert.That(results.Count).IsEqualTo(2);
    var byHandler = results.ToDictionary(r => r.HandlerId);
    await Assert.That(byHandler[handlerA].Success).IsTrue();
    await Assert.That(byHandler[handlerA].ErrorMessage).IsNull();
    await Assert.That(byHandler[handlerB].Success).IsTrue();
    await Assert.That(byHandler[handlerB].ErrorMessage).IsNull();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var remaining = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_inbox WHERE message_id = ANY(@ids)", new { ids = new[] { inboxA, inboxB } });
    await Assert.That(remaining).IsEqualTo(0L);
  }

  // ----- PurgeOrphanInboxAsync with rows -----

  [Test]
  public async Task PurgeOrphanInboxAsync_OrphanRow_DeletesAndReturnsMappedRowAsync() {
    var c = _build();
    var handledStream = (Guid)TrackedGuid.NewMedo();
    var orphanStream = (Guid)TrackedGuid.NewMedo();
    var handledId = (Guid)TrackedGuid.NewMedo();
    var orphanId = (Guid)TrackedGuid.NewMedo();

    await c.StoreInboxMessagesAsync([
      _makeInbox(handledId, handledStream, _makeHops(handledStream), messageType: "Test.Handled, Test"),
      _makeInbox(orphanId, orphanStream, _makeHops(orphanStream), messageType: "Test.Orphan, Test"),
    ], partitionCount: 100);

    var purged = await c.PurgeOrphanInboxAsync(["Test.Handled, Test"]);

    await Assert.That(purged.Count).IsEqualTo(1);
    await Assert.That(purged[0].MessageId).IsEqualTo(orphanId);
    await Assert.That(purged[0].MessageType).IsEqualTo("Test.Orphan, Test");
    await Assert.That(purged[0].HandlerName).IsEqualTo("TestHandler");

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var orphanCount = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_inbox WHERE message_id = @m", new { m = orphanId });
    await Assert.That(orphanCount).IsEqualTo(0L);
    var handledCount = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_inbox WHERE message_id = @m", new { m = handledId });
    await Assert.That(handledCount).IsEqualTo(1L)
      .Because("Rows whose message_type IS locally handled must survive the purge.");
  }

  // ----- NotifyScheduledRetryDueAsync -----

  [Test]
  public async Task NotifyScheduledRetryDueAsync_NoDueWork_ReturnsZeroAsync() {
    var c = _build();
    var n = await c.NotifyScheduledRetryDueAsync();
    await Assert.That(n).IsEqualTo(0);
  }

  // ----- FetchEventsByIdsAsync row mapping -----

  [Test]
  public async Task FetchEventsByIdsAsync_SeededEvents_MapsAllFieldsAsync() {
    var c = _build();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventA = (Guid)TrackedGuid.NewMedo();
    var eventB = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _seedEventStoreRowAsync(conn, eventA, streamId, version: 1, commitSequence: 1L);
    await _seedEventStoreRowAsync(conn, eventB, streamId, version: 2, commitSequence: 2L);

    var rows = await c.FetchEventsByIdsAsync([eventA, eventB]);

    await Assert.That(rows.Count).IsEqualTo(2);
    var byEvent = rows.ToDictionary(r => r.EventId);
    foreach (var eventId in new[] { eventA, eventB }) {
      var row = byEvent[eventId];
      await Assert.That(row.StreamId).IsEqualTo(streamId);
      await Assert.That(row.EventType).IsEqualTo("Test.OrderCreated, Test");
      await Assert.That(row.EventWorkId).IsEqualTo(Guid.Empty)
        .Because("fetch_events_by_ids intentionally returns no work id; the mapper stamps Guid.Empty.");
      using var eventData = JsonDocument.Parse(row.EventData);
      await Assert.That(eventData.RootElement.GetProperty("amount").GetInt32()).IsEqualTo(42);
      await Assert.That(row.Metadata).IsNotNull();
      using var metadata = JsonDocument.Parse(row.Metadata!);
      await Assert.That(metadata.RootElement.GetProperty("Hops").GetArrayLength()).IsEqualTo(1);
      await Assert.That(row.Scope).IsNotNull();
      using var scope = JsonDocument.Parse(row.Scope!);
      await Assert.That(scope.RootElement.GetProperty("t").GetString()).IsEqualTo("tenant-7");
    }
  }

  // ----- GetPerspectiveCursorAsync row mapping -----

  [Test]
  public async Task GetPerspectiveCursorAsync_SeededCursor_MapsAllFieldsAsync() {
    var c = _build();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "CursorPerspective";
    var lastEventId = (Guid)TrackedGuid.NewMedo();
    var rewindTriggerId = (Guid)TrackedGuid.NewMedo();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(@"
      INSERT INTO wh_perspective_cursors
        (stream_id, perspective_name, last_event_id, status, rewind_trigger_event_id)
      VALUES (@s, @p, @le, 2, @rt)",
      new { s = streamId, p = perspectiveName, le = lastEventId, rt = rewindTriggerId });

    var info = await c.GetPerspectiveCursorAsync(streamId, perspectiveName);

    await Assert.That(info).IsNotNull();
    var cursor = info!;
    await Assert.That(cursor.StreamId).IsEqualTo(streamId);
    await Assert.That(cursor.PerspectiveName).IsEqualTo(perspectiveName);
    await Assert.That(cursor.LastEventId).IsEqualTo(lastEventId);
    await Assert.That(cursor.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(cursor.RewindTriggerEventId).IsEqualTo(rewindTriggerId);
  }
}
