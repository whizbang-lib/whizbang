using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Guard-clause, gate, and serialization-edge companion to
/// <see cref="DapperWorkCoordinatorDeepPathTests"/>. Covers:
/// <list type="bullet">
///   <item><description>Constructor and per-method <see cref="ArgumentNullException"/> guards.</description></item>
///   <item><description>Empty-input short-circuits that return without touching SQL.</description></item>
///   <item><description>The <see cref="WorkCoordinatorGate"/>-provided branch of every gated method
///     (the broad suite only exercises the gate-null branch).</description></item>
///   <item><description><c>_buildFailuresByCategoryJson</c> / <c>_buildInquiriesJson</c> multi-element
///     comma branches via <c>FlushCompletionsAsync</c> and <c>ResolveSyncInquiriesAsync</c>.</description></item>
///   <item><description>Heartbeat metadata (<c>GetRawText</c>) branch, per-table recompute counts,
///     NULL-column row mapping in <c>FetchOutboxBatchAsync</c>, and the internal
///     <see cref="WorkBatchRow"/> / <see cref="SerializedWorkBatchData"/> DTO shapes that are only
///     hydrated by the retired <c>process_work_batch</c> pipeline.</description></item>
/// </list>
/// </summary>
public class DapperWorkCoordinatorGuardAndGateTests : PostgresTestBase {

  private readonly JsonSerializerOptions _jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private DapperWorkCoordinator _build() {
    return new DapperWorkCoordinator(
      ConnectionString,
      _jsonOptions,
      NullLogger<DapperWorkCoordinator>.Instance);
  }

  private static OutboxMessage _makeOutbox(Guid msgId, Guid? streamId, string? destination = "guard-topic") {
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(msgId),
      JsonDocument.Parse("{\"k\":1}").RootElement,
      []);
    return new OutboxMessage {
      MessageId = msgId,
      Destination = destination,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Test.X, Test",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      StreamId = streamId,
      IsEvent = false,
    };
  }

  private static InboxMessage _makeInbox(Guid msgId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(msgId),
      JsonDocument.Parse("{\"p\":1}").RootElement,
      []);
    return new InboxMessage {
      MessageId = msgId,
      HandlerName = "GuardHandler",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Test.X, Test",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      StreamId = streamId,
      IsEvent = true,
    };
  }

  private static async Task<bool> _throwsArgumentNullAsync(Func<Task> action) {
    try {
      await action();
    } catch (ArgumentNullException) {
      return true;
    }
    return false;
  }

  // ----- constructor guards -----

  [Test]
  public async Task Constructor_NullConnectionString_ThrowsAsync() {
    var threw = false;
    try {
      _ = new DapperWorkCoordinator(null!, _jsonOptions);
    } catch (ArgumentNullException) { threw = true; }
    await Assert.That(threw).IsTrue();
  }

  [Test]
  public async Task Constructor_NullJsonOptions_ThrowsAsync() {
    var threw = false;
    try {
      _ = new DapperWorkCoordinator(ConnectionString, null!);
    } catch (ArgumentNullException) { threw = true; }
    await Assert.That(threw).IsTrue();
  }

  // ----- per-method ArgumentNullException guards -----

  [Test]
  public async Task NullArguments_GuardedMethods_ThrowArgumentNullAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await Assert.That(await _throwsArgumentNullAsync(() => c.FetchOutboxBatchAsync(null!, instanceId))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.FetchInboxBatchAsync(null!, instanceId))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.FetchPendingPerspectiveEventsAsync(streamId, null!, instanceId))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.ClaimAndFetchPendingPerspectiveEventsAsync(streamId, null!, instanceId, TimeSpan.FromMinutes(1)))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.FetchEventsByIdsAsync(null!))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.PurgeOrphanInboxAsync(null!))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.CompleteOutboxPublishedAsync(null!, debugMode: false))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.CompletePerspectiveAsync(null!, [], debugMode: false))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.CompletePerspectiveAsync([], null!, debugMode: false))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.ReportFailuresAsync(WorkCategory.Outbox, null!))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.RenewLeasesAsync(WorkCategory.Outbox, null!))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.CommitHandlerResultAsync(null!))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.CommitHandlerBatchAsync(null!))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.FlushCompletionsAsync(null!))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.ResolveSyncInquiriesAsync(null!))).IsTrue();
    await Assert.That(await _throwsArgumentNullAsync(() => c.ClaimWorkAsync(null!))).IsTrue();
  }

  // ----- empty-input short-circuits -----

  [Test]
  public async Task EmptyInputs_ShortCircuitPathsReturnDefaultsAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();

    var outboxRows = await c.FetchOutboxBatchAsync([], instanceId);
    await Assert.That(outboxRows.Count).IsEqualTo(0);

    var inboxRows = await c.FetchInboxBatchAsync([], instanceId);
    await Assert.That(inboxRows.Count).IsEqualTo(0);

    // Both lists empty: CompletePerspectiveAsync returns before any SQL.
    await c.CompletePerspectiveAsync([], [], debugMode: false);

    // The null-list branch (distinct from the empty-list branch covered by the broad suite).
    var cleaned = await c.CleanupCompletedStreamsAsync(null!);
    await Assert.That(cleaned).IsEqualTo(0);

    await c.StoreInboxMessagesAsync([], partitionCount: 100);
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var inboxCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_inbox");
    await Assert.That(inboxCount).IsEqualTo(0L);
  }

  // ----- RecordHeartbeatAsync metadata branch -----

  [Test]
  public async Task RecordHeartbeatAsync_WithMetadata_PersistsMetadataJsonAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    using var doc = JsonDocument.Parse("{\"zone\":\"z1\"}");

    await c.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "svc-meta", "host-meta", 5, doc.RootElement));

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var zone = await conn.ExecuteScalarAsync<string>(
      "SELECT metadata->>'zone' FROM wh_service_instances WHERE instance_id = @id",
      new { id = instanceId });
    await Assert.That(zone).IsEqualTo("z1");
  }

  // ----- WorkCoordinatorGate-provided branch of every gated method -----

  [Test]
  public async Task GatedCoordinator_AllGatedMethods_ExecuteThroughGateAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 2);
    var c = new DapperWorkCoordinator(
      ConnectionString,
      _jsonOptions,
      NullLogger<DapperWorkCoordinator>.Instance,
      commandTimeoutSeconds: 5,
      gate: gate);
    var instanceId = (Guid)TrackedGuid.NewMedo();

    await c.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "svc-gate", "host-gate", 11));

    // Queues empty here — ClaimWorkAsync's gate branch plus the empty-batch path.
    var emptyBatch = await c.ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc-gate", "host-gate", 11,
      MaxStreams: 10, PartitionCount: 100, LeaseSeconds: 300));
    await Assert.That(emptyBatch.PerspectiveStreamIds.Count).IsEqualTo(0);

    var due = await c.NotifyScheduledRetryDueAsync();
    await Assert.That(due).IsEqualTo(0);

    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, streamId)], partitionCount: 100);

    var renewed = await c.RenewLeasesAsync(WorkCategory.Outbox, [msgId], leaseSeconds: 120);
    await Assert.That(renewed).IsEqualTo(1);

    await c.ReportFailuresAsync(WorkCategory.Outbox, [
      new MessageFailure {
        MessageId = msgId,
        CompletedStatus = MessageProcessingStatus.Stored,
        Error = "gated failure",
      },
    ]);

    var published = await c.CompleteOutboxPublishedAsync([msgId], debugMode: false);
    await Assert.That(published).IsGreaterThanOrEqualTo(1);

    await c.CompletePerspectiveAsync(
      [new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = "GatePerspective",
        LastEventId = msgId,
        ProcessedEventIds = [],
        Status = PerspectiveProcessingStatus.Completed,
      }],
      eventWorkIds: [],
      debugMode: false);

    var inboxA = (Guid)TrackedGuid.NewMedo();
    var inboxB = (Guid)TrackedGuid.NewMedo();
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    await c.StoreInboxMessagesAsync([_makeInbox(inboxA, streamA), _makeInbox(inboxB, streamB)], partitionCount: 100);

    await c.CommitHandlerResultAsync(new HandlerCommitRequest(
      HandlerId: (Guid)TrackedGuid.NewMedo(),
      InstanceId: instanceId,
      ServiceName: "svc-gate",
      HostName: "host-gate",
      ProcessId: 11,
      PartitionCount: 100,
      InboxCompletion: new HandlerInboxCompletion(inboxA, Status: 2)));

    var batchResults = await c.CommitHandlerBatchAsync([
      new HandlerCommitRequest(
        HandlerId: (Guid)TrackedGuid.NewMedo(),
        InstanceId: instanceId,
        ServiceName: "svc-gate",
        HostName: "host-gate",
        ProcessId: 11,
        PartitionCount: 100,
        InboxCompletion: new HandlerInboxCompletion(inboxB, Status: 2)),
    ]);
    await Assert.That(batchResults.Count).IsEqualTo(1);
    await Assert.That(batchResults[0].Success).IsTrue();

    await c.FlushCompletionsAsync(new FlushCompletionsRequest());

    var syncResults = await c.ResolveSyncInquiriesAsync([
      new SyncInquiry { StreamId = streamB, PerspectiveName = "GatePerspective" },
    ]);
    await Assert.That(syncResults.Count).IsEqualTo(1);
  }

  // ----- FlushCompletionsAsync: multi-category failures (comma branch) -----

  [Test]
  public async Task FlushCompletionsAsync_TwoFailureCategories_PersistsBothCategoriesAsync() {
    var c = _build();
    var outboxId = (Guid)TrackedGuid.NewMedo();
    var inboxId = (Guid)TrackedGuid.NewMedo();
    var outboxStream = (Guid)TrackedGuid.NewMedo();
    var inboxStream = (Guid)TrackedGuid.NewMedo();

    await c.StoreOutboxMessagesAsync([_makeOutbox(outboxId, outboxStream)], partitionCount: 100);
    await c.StoreInboxMessagesAsync([_makeInbox(inboxId, inboxStream)], partitionCount: 100);

    await c.FlushCompletionsAsync(new FlushCompletionsRequest(
      FailuresByCategory: [
        new CategoryFailures(WorkCategory.Outbox, [
          new MessageFailure {
            MessageId = outboxId,
            CompletedStatus = MessageProcessingStatus.Stored,
            Error = "outbox flush failure",
          },
        ]),
        new CategoryFailures(WorkCategory.Inbox, [
          new MessageFailure {
            MessageId = inboxId,
            CompletedStatus = MessageProcessingStatus.Stored,
            Error = "inbox flush failure",
          },
        ]),
      ]));

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var outboxError = await conn.ExecuteScalarAsync<string?>(
      "SELECT error FROM wh_outbox WHERE message_id = @m", new { m = outboxId });
    await Assert.That(outboxError).IsEqualTo("outbox flush failure");
    var inboxError = await conn.ExecuteScalarAsync<string?>(
      "SELECT error FROM wh_inbox WHERE message_id = @m", new { m = inboxId });
    await Assert.That(inboxError).IsEqualTo("inbox flush failure");
  }

  // ----- ResolveSyncInquiriesAsync: multiple inquiries + id-list flags -----

  [Test]
  public async Task ResolveSyncInquiriesAsync_TwoInquiries_ReturnsPerInquiryCountsAsync() {
    var c = _build();
    var pendingStream = (Guid)TrackedGuid.NewMedo();
    var emptyStream = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "SyncPersp";

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(@"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type,
         event_data, metadata, created_at, commit_sequence)
      VALUES (@id, @stream, @stream, 'TestAgg', 1, 'Test.OrderCreated, Test',
              '{}'::jsonb, '{}'::jsonb, NOW(), 1)",
      new { id = eventId, stream = pendingStream });
    await conn.ExecuteAsync(@"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
         partition_number, status, attempts, created_at, claimed_at, processed_at)
      VALUES (@work, @stream, @persp, @event, NULL, NULL, 0, 0, 0, NOW(), NULL, NULL)",
      new { work = workId, stream = pendingStream, persp = perspectiveName, @event = eventId });

    var inquiryA = new SyncInquiry {
      StreamId = pendingStream,
      PerspectiveName = perspectiveName,
      IncludePendingEventIds = true,
      IncludeProcessedEventIds = true,
      DiscoverPendingFromOutbox = true,
    };
    var inquiryB = new SyncInquiry {
      StreamId = emptyStream,
      PerspectiveName = perspectiveName,
    };

    var results = await c.ResolveSyncInquiriesAsync([inquiryA, inquiryB]);

    await Assert.That(results.Count).IsEqualTo(2);
    var byInquiry = results.ToDictionary(r => r.InquiryId);
    await Assert.That(byInquiry[inquiryA.InquiryId].StreamId).IsEqualTo(pendingStream);
    await Assert.That(byInquiry[inquiryA.InquiryId].PendingCount).IsEqualTo(1);
    await Assert.That(byInquiry[inquiryA.InquiryId].ProcessedCount).IsEqualTo(0);
    await Assert.That(byInquiry[inquiryB.InquiryId].StreamId).IsEqualTo(emptyStream);
    await Assert.That(byInquiry[inquiryB.InquiryId].PendingCount).IsEqualTo(0);
    await Assert.That(byInquiry[inquiryB.InquiryId].ProcessedCount).IsEqualTo(0);
  }

  // ----- RecomputePartitionNumbersAsync: per-table counts with mismatched rows -----

  [Test]
  public async Task RecomputePartitionNumbersAsync_MismatchedRows_ReportsPerTableCountsAsync() {
    var c = _build();
    var outboxId = (Guid)TrackedGuid.NewMedo();
    var inboxId = (Guid)TrackedGuid.NewMedo();
    var outboxStream = (Guid)TrackedGuid.NewMedo();
    var inboxStream = (Guid)TrackedGuid.NewMedo();
    var orphanStream = (Guid)TrackedGuid.NewMedo();

    await c.StoreOutboxMessagesAsync([_makeOutbox(outboxId, outboxStream)], partitionCount: 100);
    await c.StoreInboxMessagesAsync([_makeInbox(inboxId, inboxStream)], partitionCount: 100);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // 9999 is outside any partition range so both rows are guaranteed mismatches.
    await conn.ExecuteAsync("UPDATE wh_outbox SET partition_number = 9999 WHERE message_id = @m", new { m = outboxId });
    await conn.ExecuteAsync("UPDATE wh_inbox SET partition_number = 9999 WHERE message_id = @m", new { m = inboxId });
    await conn.ExecuteAsync(@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry)
      VALUES (@s, 9999, NULL, NULL)",
      new { s = orphanStream });

    var result = await c.RecomputePartitionNumbersAsync(partitionCount: 7);

    await Assert.That(result.OutboxRowsRecomputed).IsEqualTo(1L);
    await Assert.That(result.InboxRowsRecomputed).IsEqualTo(1L);
    // The two store calls also upserted wh_active_streams rows whose partition (mod 100)
    // may or may not coincide with mod 7 — only the 9999 row is a guaranteed mismatch.
    await Assert.That(result.ActiveStreamsRowsRecomputed).IsGreaterThanOrEqualTo(1L);
    await Assert.That(result.AnyRecomputed).IsTrue();
  }

  // ----- FetchOutboxBatchAsync: NULL-column row mapping via the singleton-stream sentinel -----

  [Test]
  public async Task FetchOutboxBatchAsync_NullStreamAndDestination_MapsNullColumnsAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    // StreamId null → wh_outbox.stream_id NULL and partition_number NULL;
    // Destination null → destination NULL. The row is fetched via the
    // message_id-as-sentinel branch of fetch_outbox_batch (v0.658 slice 7).
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, streamId: null, destination: null)], partitionCount: 100);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(
      "UPDATE wh_outbox SET instance_id = @i, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @m",
      new { i = instanceId, m = msgId });

    var rows = await c.FetchOutboxBatchAsync([msgId], instanceId, maxPerStream: 10);

    await Assert.That(rows.Count).IsEqualTo(1);
    var row = rows[0];
    await Assert.That(row.MessageId).IsEqualTo(msgId);
    await Assert.That(row.StreamId).IsNull();
    await Assert.That(row.Destination).IsNull();
    await Assert.That(row.PartitionNumber).IsNull();
    await Assert.That(row.IsEvent).IsFalse();
    await Assert.That(row.Error).IsNull();
  }

  // ----- Internal DTOs only hydrated by the retired process_work_batch pipeline -----
  //
  // ProcessWorkBatchAsync is now an IWorkCoordinator default-interface method returning an
  // empty batch, so nothing in DapperWorkCoordinator hydrates WorkBatchRow or builds
  // SerializedWorkBatchData at runtime. Direct construction (via InternalsVisibleTo) is the
  // only remaining execution path; these tests lock the DTO shapes until the Phase C cleanup
  // either rewires or removes them.

  [Test]
  public async Task WorkBatchRow_AllProperties_RoundTripAsync() {
    var workId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    var row = new WorkBatchRow {
      instance_rank = 1,
      active_instance_count = 3,
      source = "outbox",
      work_id = workId,
      work_stream_id = streamId,
      partition_number = 7,
      destination = "topic-a",
      message_type = "Test.X, Test",
      envelope_type = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      message_data = "{\"k\":1}",
      metadata = "{}",
      status = 1,
      attempts = 2,
      is_newly_stored = true,
      is_orphaned = false,
      perspective_name = "RowPerspective",
    };

    await Assert.That(row.instance_rank).IsEqualTo(1);
    await Assert.That(row.active_instance_count).IsEqualTo(3);
    await Assert.That(row.source).IsEqualTo("outbox");
    await Assert.That(row.work_id).IsEqualTo(workId);
    await Assert.That(row.work_stream_id).IsEqualTo(streamId);
    await Assert.That(row.partition_number).IsEqualTo(7);
    await Assert.That(row.destination).IsEqualTo("topic-a");
    await Assert.That(row.message_type).IsEqualTo("Test.X, Test");
    await Assert.That(row.envelope_type).IsEqualTo("Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core");
    await Assert.That(row.message_data).IsEqualTo("{\"k\":1}");
    await Assert.That(row.metadata).IsEqualTo("{}");
    await Assert.That(row.status).IsEqualTo(1);
    await Assert.That(row.attempts).IsEqualTo(2);
    await Assert.That(row.is_newly_stored).IsTrue();
    await Assert.That(row.is_orphaned).IsFalse();
    await Assert.That(row.perspective_name).IsEqualTo("RowPerspective");
  }

  [Test]
  public async Task SerializedWorkBatchData_AllProperties_RoundTripAsync() {
    var data = new SerializedWorkBatchData(
      OutboxCompletions: "[1]",
      OutboxFailures: "[2]",
      InboxCompletions: "[3]",
      InboxFailures: "[4]",
      PerspectiveEventCompletions: "[5]",
      PerspectiveCompletions: "[6]",
      PerspectiveFailures: "[7]",
      NewOutboxMessages: "[8]",
      NewInboxMessages: "[9]",
      Metadata: "{\"m\":10}",
      RenewOutboxLeaseIds: "[11]",
      RenewInboxLeaseIds: "[12]",
      SyncInquiries: "[13]");

    await Assert.That(data.OutboxCompletions).IsEqualTo("[1]");
    await Assert.That(data.OutboxFailures).IsEqualTo("[2]");
    await Assert.That(data.InboxCompletions).IsEqualTo("[3]");
    await Assert.That(data.InboxFailures).IsEqualTo("[4]");
    await Assert.That(data.PerspectiveEventCompletions).IsEqualTo("[5]");
    await Assert.That(data.PerspectiveCompletions).IsEqualTo("[6]");
    await Assert.That(data.PerspectiveFailures).IsEqualTo("[7]");
    await Assert.That(data.NewOutboxMessages).IsEqualTo("[8]");
    await Assert.That(data.NewInboxMessages).IsEqualTo("[9]");
    await Assert.That(data.Metadata).IsEqualTo("{\"m\":10}");
    await Assert.That(data.RenewOutboxLeaseIds).IsEqualTo("[11]");
    await Assert.That(data.RenewInboxLeaseIds).IsEqualTo("[12]");
    await Assert.That(data.SyncInquiries).IsEqualTo("[13]");
  }
}
