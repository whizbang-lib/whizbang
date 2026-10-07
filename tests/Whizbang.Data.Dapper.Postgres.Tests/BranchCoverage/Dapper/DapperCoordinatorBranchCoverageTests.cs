// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests.BranchCoverage.DapperDriver;

/// <summary>
/// Branch outcomes of the Dapper work coordinator, dead-letter store and schema initializer that
/// need a real database: id lists handed in as arrays (every other suite builds them with
/// collection expressions, which the compiler materializes as its own read-only list types), a gated
/// applied-status read, a claim that returns receptor work, a debug-mode handler commit, a gated
/// dead-letter move, and a rollback whose backup table yields no original name.
/// </summary>
public class DapperCoordinatorBranchCoverageTests : PostgresTestBase {
  private readonly JsonSerializerOptions _jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private DapperWorkCoordinator _build(WorkCoordinatorGate? gate = null) =>
    new(ConnectionString, _jsonOptions, NullLogger<DapperWorkCoordinator>.Instance, gate);

  // ========================================
  // ID ARRAYS
  // ========================================

  [Test]
  public async Task FetchOutboxBatchAsync_StreamIdArray_ReturnsTheLeasedRowAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();
    var msgId = (Guid)TrackedGuid.New();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, streamId)], partitionCount: 100);
    await _executeAsync(
      "UPDATE wh_outbox SET instance_id = @i, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @m",
      new { i = instanceId, m = msgId });

    Guid[] input1 = [streamId];
    var rows = await c.FetchOutboxBatchAsync(input1, instanceId, maxPerStream: 10);

    await Assert.That(rows.Select(r => r.MessageId)).IsEquivalentTo([msgId]);
  }

  [Test]
  public async Task FetchInboxBatchAsync_StreamIdArray_ReturnsTheLeasedRowAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();
    var msgId = (Guid)TrackedGuid.New();
    await c.StoreInboxMessagesAsync([_makeInbox(msgId, streamId)], partitionCount: 100);
    await _executeAsync(
      "UPDATE wh_inbox_state SET instance_id = @i, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @m",
      new { i = instanceId, m = msgId });

    Guid[] input2 = [streamId];
    var rows = await c.FetchInboxBatchAsync(input2, instanceId, maxPerStream: 10);

    await Assert.That(rows.Select(r => r.MessageId)).IsEquivalentTo([msgId]);
  }

  [Test]
  public async Task FetchEventsByIdsAsync_EventIdArray_ReturnsTheSeededEventAsync() {
    var c = _build();
    var streamId = (Guid)TrackedGuid.New();
    var eventId = (Guid)TrackedGuid.New();
    await _executeAsync("""
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at, commit_sequence)
      VALUES (@id, @stream, @stream, 'TestAgg', 1, 'Test.OrderCreated, Test', '{"t": "tenant-7"}'::jsonb, NOW(), 1);
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      VALUES (@id, '{"amount": 42}'::jsonb, '{"Hops": []}'::jsonb)
      """,
      new { id = eventId, stream = streamId });

    Guid[] input3 = [eventId];
    var rows = await c.FetchEventsByIdsAsync(input3);

    await Assert.That(rows.Select(r => r.EventId)).IsEquivalentTo([eventId]);
  }

  [Test]
  public async Task CompleteOutboxPublishedAsync_IdArray_DeletesThePublishedRowAsync() {
    var c = _build();
    var msgId = (Guid)TrackedGuid.New();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, (Guid)TrackedGuid.New())], partitionCount: 100);

    Guid[] input4 = [msgId];
    var completed = await c.CompleteOutboxPublishedAsync(input4, debugMode: false);

    await Assert.That(completed).IsGreaterThanOrEqualTo(1)
      .Because("the id handed in as an array reaches the completion function");
  }

  [Test]
  public async Task RenewLeasesAsync_IdArray_RenewsTheRowAsync() {
    var c = _build();
    var msgId = (Guid)TrackedGuid.New();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, (Guid)TrackedGuid.New())], partitionCount: 100);

    Guid[] input5 = [msgId];
    var renewed = await c.RenewLeasesAsync(WorkCategory.Outbox, input5, leaseSeconds: 120);

    await Assert.That(renewed).IsEqualTo(1);
  }

  [Test]
  public async Task PurgeOrphanInboxAsync_HandledTypeArray_PurgesOnlyTheOrphanAsync() {
    var c = _build();
    var handledId = (Guid)TrackedGuid.New();
    var orphanId = (Guid)TrackedGuid.New();
    await c.StoreInboxMessagesAsync([
      _makeInbox(handledId, (Guid)TrackedGuid.New(), "Test.Handled, Test"),
      _makeInbox(orphanId, (Guid)TrackedGuid.New(), "Test.Orphan, Test")
    ], partitionCount: 100);

    string[] input6 = ["Test.Handled, Test"];
    var purged = await c.PurgeOrphanInboxAsync(input6);

    await Assert.That(purged.Select(p => p.MessageId)).IsEquivalentTo([orphanId]);
    await Assert.That(await _countAsync("SELECT COUNT(*) FROM wh_inbox WHERE message_id = @m", handledId)).IsEqualTo(1L);
  }

  /// <summary>
  /// Array-typed completion lists are used as they are: the outbox id is completed, and an empty
  /// array of perspective work completes nothing else.
  /// </summary>
  [Test]
  public async Task FlushCompletionsAsync_IdArrays_CompletesTheOutboxRowAsync() {
    var c = _build();
    var msgId = (Guid)TrackedGuid.New();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, (Guid)TrackedGuid.New())], partitionCount: 100);

    Guid[] outboxIds = [msgId];
    Guid[] perspectiveEventWorkIds = [];
    await c.FlushCompletionsAsync(new FlushCompletionsRequest(
      OutboxIds: outboxIds,
      PerspectiveEventWorkIds: perspectiveEventWorkIds));

    await Assert.That(await _countAsync("SELECT COUNT(*) FROM wh_outbox WHERE message_id = @m", msgId)).IsEqualTo(0L)
      .Because("production-mode outbox completion deletes the published row");
  }

  // ========================================
  // GATED READ, CLAIM, DEBUG COMMIT
  // ========================================

  /// <summary>The applied-status read goes through the gate when one is configured, and releases its slot.</summary>
  [Test]
  public async Task GetAppliedEventStatusAsync_WithGate_AcquiresAndReleasesASlotAsync() {
    var gateLogger = new GateLogger();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, logger: gateLogger);
    var eventId = (Guid)TrackedGuid.New();

    var status = await _build(gate).GetAppliedEventStatusAsync(new AppliedEventInquiry("CoveragePerspective", eventId));

    await Assert.That(status!.Value.State).IsEqualTo(AppliedEventState.NotArrived);
    await Assert.That(gateLogger.Messages.Any(m => m.Contains("AcquireAsync entered", StringComparison.Ordinal))).IsTrue();
    await Assert.That(gate.SnapshotHolders()).IsEmpty()
      .Because("the slot is released when the read completes");
  }

  /// <summary>
  /// Receptor work owned by this instance comes back from the claim with its own source, which the
  /// Dapper claim neither treats as stream work nor refuses as unsupported outbox or inbox work.
  /// </summary>
  [Test]
  public async Task ClaimWorkAsync_OwnedReceptorWork_IsNeitherStreamWorkNorRefusedAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.New();
    await c.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "svc-claim-r", "host-claim-r", 1));
    var receptorWorkId = (Guid)TrackedGuid.New();
    await _executeAsync(@"
      INSERT INTO wh_receptor_processing (id, event_id, receptor_name, stream_id, instance_id, lease_expiry)
      VALUES (@id, @eventId, 'CoverageReceptor', @streamId, @instanceId, NOW() + INTERVAL '5 minutes')",
      new { id = receptorWorkId, eventId = (Guid)TrackedGuid.New(), streamId = (Guid)TrackedGuid.New(), instanceId });

    var batch = await c.ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc-claim-r", "host-claim-r", 1,
      MaxStreams: 50, PartitionCount: 100, LeaseSeconds: 300));

    await Assert.That(batch.PerspectiveStreamIds).IsEmpty();
    await Assert.That(await _countAsync("SELECT COUNT(*) FROM wh_receptor_processing WHERE id = @m", receptorWorkId))
      .IsEqualTo(1L);
  }

  /// <summary>
  /// A handler commit in debug mode keeps the acknowledged inbox row (stamped processed) instead of
  /// deleting it, which is what debug mode exists for.
  /// </summary>
  [Test]
  public async Task CommitHandlerResultAsync_DebugMode_RetainsTheAcknowledgedInboxRowAsync() {
    var c = _build();
    var inboxId = (Guid)TrackedGuid.New();
    await c.StoreInboxMessagesAsync([_makeInbox(inboxId, (Guid)TrackedGuid.New())], partitionCount: 100);

    await c.CommitHandlerResultAsync(new HandlerCommitRequest(
      HandlerId: (Guid)TrackedGuid.New(),
      InstanceId: (Guid)TrackedGuid.New(),
      ServiceName: "svc-debug",
      HostName: "host-debug",
      ProcessId: 3,
      PartitionCount: 100,
      InboxCompletion: new HandlerInboxCompletion(inboxId, Status: 2),
      DebugMode: true));

    await Assert.That(await _countAsync("SELECT COUNT(*) FROM wh_inbox WHERE message_id = @m", inboxId)).IsEqualTo(1L)
      .Because("production mode deletes the acknowledged row; debug mode keeps it for inspection");
  }

  // ========================================
  // DEAD-LETTER STORE, SCHEMA ROLLBACK
  // ========================================

  /// <summary>A dead-letter move goes through the gate when one is configured.</summary>
  [Test]
  public async Task DeadLetterMoveAsync_WithGate_AcquiresASlotAsync() {
    var gateLogger = new GateLogger();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, logger: gateLogger);
    var store = new DapperDeadLetterStore(ConnectionString, gate);

    var result = await store.MoveAsync(
      deadLetterId: (Guid)TrackedGuid.New(),
      sourceTable: DeadLetterSourceTable.OUTBOX,
      sourceId: (Guid)TrackedGuid.New(),
      failureReason: MessageFailureReason.MaxAttemptsExceeded,
      errorText: "ghost",
      instanceId: (Guid)TrackedGuid.New(),
      generation: "coverage");

    await Assert.That(result).IsNull()
      .Because("a source row that was never there has nothing to move");
    await Assert.That(gateLogger.Messages.Any(m => m.Contains("AcquireAsync entered", StringComparison.Ordinal))).IsTrue();
    await Assert.That(gate.SnapshotHolders()).IsEmpty();
  }

  /// <summary>
  /// A backup table named with nothing before its backup marker yields an empty original name,
  /// which is not a safe identifier: the rollback refuses and leaves the table where it is.
  /// </summary>
  [Test]
  public async Task RollbackAsync_BackupWithEmptyOriginalName_RefusesAndLeavesTheTableAsync() {
    await _executeAsync("CREATE TABLE _bak_20240101 (id integer)", new { });

    var rolledBack = await new PostgresSchemaInitializer(ConnectionString).RollbackAsync("perspective:Coverage");

    await Assert.That(rolledBack).IsFalse();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var remaining = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '_bak_20240101'");
    await Assert.That(remaining).IsEqualTo(1L)
      .Because("a refused rollback renames nothing");
  }

  // ========================================
  // HELPERS
  // ========================================

  private async Task _executeAsync(string sql, object parameters) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(sql, parameters);
  }

  private async Task<long> _countAsync(string sql, Guid id) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return await conn.ExecuteScalarAsync<long>(sql, new { m = id });
  }

  private static OutboxMessage _makeOutbox(Guid msgId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(msgId),
      JsonDocument.Parse("{\"k\":1}").RootElement,
      []);
    return new OutboxMessage {
      MessageId = msgId,
      Destination = "coverage-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Test.X, Test",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      StreamId = streamId,
      IsEvent = false,
    };
  }

  private static InboxMessage _makeInbox(Guid msgId, Guid streamId, string messageType = "Test.X, Test") {
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(msgId),
      JsonDocument.Parse("{\"p\":1}").RootElement,
      []);
    return new InboxMessage {
      MessageId = msgId,
      HandlerName = "CoverageHandler",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = messageType,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      StreamId = streamId,
      IsEvent = true,
    };
  }
}

/// <summary>A gate logger with every level enabled that records rendered messages.</summary>
sealed file class GateLogger : ILogger<WorkCoordinatorGate> {
  private readonly List<string> _messages = [];

  public IReadOnlyList<string> Messages {
    get {
      lock (_messages) {
        return [.. _messages];
      }
    }
  }

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
    var message = formatter(state, exception);
    lock (_messages) {
      _messages.Add(message);
    }
  }
}
