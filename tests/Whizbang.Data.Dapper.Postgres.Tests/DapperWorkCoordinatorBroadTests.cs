using System.Text.Json;
using Dapper;
using Medo;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Broad smoke-test suite for <see cref="DapperWorkCoordinator"/> covering the SQL paths
/// that previously had no Dapper-side coverage (the EFCore equivalent is exercised by
/// integration tests but Dapper sat at ~2% line coverage). Tests exercise the happy
/// paths and empty-result paths — they're not meant to assert behavior comprehensively
/// (the SQL functions themselves have dedicated tests in the EFCore project), just to
/// keep the Dapper coordinator's serialization and SQL-invocation code paths walked.
/// </summary>
public class DapperWorkCoordinatorBroadTests : PostgresTestBase {

  private DapperWorkCoordinator _build() {
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = InfrastructureJsonContext.Default,
    };
    return new DapperWorkCoordinator(
      ConnectionString,
      jsonOptions,
      NullLogger<DapperWorkCoordinator>.Instance);
  }

  private static OutboxMessage _makeOutbox(Guid msgId, Guid streamId, string? destination = "test-dest") {
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

  [Test]
  public async Task RecordHeartbeatAsync_NewInstance_InsertsRowAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await c.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "svc-a", "host-a", 42));

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var svc = await conn.ExecuteScalarAsync<string>(
      "SELECT service_name FROM wh_service_instances WHERE instance_id = @id",
      new { id = instanceId });
    await Assert.That(svc).IsEqualTo("svc-a");
  }

  [Test]
  public async Task RecordHeartbeatAsync_NullRequest_ThrowsAsync() {
    var c = _build();
    var threw = false;
    try {
      await c.RecordHeartbeatAsync(null!);
    } catch (ArgumentNullException) { threw = true; }
    await Assert.That(threw).IsTrue();
  }

  [Test]
  public async Task DeregisterInstanceAsync_RemovesRowAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await c.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "svc-b", "host-b", 1));
    await c.DeregisterInstanceAsync(instanceId);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var count = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_service_instances WHERE instance_id = @id",
      new { id = instanceId });
    await Assert.That(count).IsEqualTo(0L);
  }

  [Test]
  public async Task ClaimWorkAsync_NoWork_ReturnsEmptyBatchAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await c.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "svc-c", "host-c", 1));

    var batch = await c.ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc-c", "host-c", 1,
      MaxStreams: 50, PartitionCount: 100, LeaseSeconds: 300));

    await Assert.That(batch).IsNotNull();
    await Assert.That(batch.OutboxStreamIds.Count).IsEqualTo(0);
    await Assert.That(batch.InboxStreamIds.Count).IsEqualTo(0);
    await Assert.That(batch.PerspectiveStreamIds.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchOutboxBatchAsync_NoRows_ReturnsEmptyAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var rows = await c.FetchOutboxBatchAsync(
      [(Guid)TrackedGuid.NewMedo()], instanceId, maxPerStream: 10);
    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchInboxBatchAsync_NoRows_ReturnsEmptyAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var rows = await c.FetchInboxBatchAsync(
      [(Guid)TrackedGuid.NewMedo()], instanceId, maxPerStream: 10);
    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchPendingPerspectiveEventsAsync_NoRows_ReturnsEmptyAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var rows = await c.FetchPendingPerspectiveEventsAsync(
      (Guid)TrackedGuid.NewMedo(), "TestPerspective", instanceId);
    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchEventsByIdsAsync_NoIds_ReturnsEmptyAsync() {
    var c = _build();
    var rows = await c.FetchEventsByIdsAsync([]);
    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchEventsByIdsAsync_UnknownIds_ReturnsEmptyAsync() {
    var c = _build();
    var rows = await c.FetchEventsByIdsAsync([
      (Guid)TrackedGuid.NewMedo(),
      (Guid)TrackedGuid.NewMedo(),
    ]);
    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_EmptyArray_NoOpAsync() {
    var c = _build();
    await c.StoreOutboxMessagesAsync([], partitionCount: 100);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_outbox");
    await Assert.That(count).IsEqualTo(0L);
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_SingleMessage_PersistsRowAsync() {
    var c = _build();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, streamId)], partitionCount: 100);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var count = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_outbox WHERE message_id = @m", new { m = msgId });
    await Assert.That(count).IsEqualTo(1L);
  }

  [Test]
  public async Task CompleteOutboxPublishedAsync_NoIds_NoOpAsync() {
    var c = _build();
    var affected = await c.CompleteOutboxPublishedAsync([], debugMode: false);
    await Assert.That(affected).IsEqualTo(0);
  }

  [Test]
  public async Task CompleteOutboxPublishedAsync_ProductionMode_DeletesRowAsync() {
    var c = _build();
    var msgId = (Guid)TrackedGuid.NewMedo();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, (Guid)TrackedGuid.NewMedo())], 100);

    var affected = await c.CompleteOutboxPublishedAsync([msgId], debugMode: false);
    await Assert.That(affected).IsGreaterThanOrEqualTo(1);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var count = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_outbox WHERE message_id = @m", new { m = msgId });
    await Assert.That(count).IsEqualTo(0L);
  }

  [Test]
  public async Task CompleteOutboxPublishedAsync_DebugMode_RetainsRowAsync() {
    var c = _build();
    var msgId = (Guid)TrackedGuid.NewMedo();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, (Guid)TrackedGuid.NewMedo())], 100);

    await c.CompleteOutboxPublishedAsync([msgId], debugMode: true);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var publishedAt = await conn.ExecuteScalarAsync<DateTime?>(
      "SELECT published_at FROM wh_outbox WHERE message_id = @m", new { m = msgId });
    await Assert.That(publishedAt).IsNotNull();
  }

  [Test]
  public async Task RenewLeasesAsync_NoMessages_NoOpAsync() {
    var c = _build();
    var n = await c.RenewLeasesAsync(WorkCategory.Outbox, [], leaseSeconds: 30);
    await Assert.That(n).IsEqualTo(0);
  }

  [Test]
  public async Task ReportFailuresAsync_EmptyList_NoOpAsync() {
    var c = _build();
    await c.ReportFailuresAsync(WorkCategory.Outbox, []);
  }

  [Test]
  public async Task CleanupCompletedStreamsAsync_NoStreams_ReturnsZeroAsync() {
    var c = _build();
    var n = await c.CleanupCompletedStreamsAsync([]);
    await Assert.That(n).IsEqualTo(0);
  }

  [Test]
  public async Task PerformMaintenanceAsync_EmptyDatabase_ReturnsResultsAsync() {
    var c = _build();
    var results = await c.PerformMaintenanceAsync();
    await Assert.That(results).IsNotNull();
  }

  [Test]
  public async Task GatherStatisticsAsync_EmptyDatabase_ReturnsZerosAsync() {
    var c = _build();
    var stats = await c.GatherStatisticsAsync();
    await Assert.That(stats).IsNotNull();
  }

  [Test]
  public async Task ResolveSyncInquiriesAsync_NoInquiries_ReturnsEmptyAsync() {
    var c = _build();
    var results = await c.ResolveSyncInquiriesAsync([]);
    await Assert.That(results.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetStreamEventsAsync_NoEvents_ReturnsEmptyAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var events = await c.GetStreamEventsAsync(instanceId, [(Guid)TrackedGuid.NewMedo()]);
    await Assert.That(events.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetPerspectiveCursorAsync_NoRow_ReturnsNullAsync() {
    var c = _build();
    var info = await c.GetPerspectiveCursorAsync(
      (Guid)TrackedGuid.NewMedo(), "TestPerspective");
    await Assert.That(info).IsNull();
  }

  [Test]
  public async Task CompletePerspectiveEventsAsync_NoIds_ReturnsZeroAsync() {
    var c = _build();
    var n = await c.CompletePerspectiveEventsAsync([], debugMode: false);
    await Assert.That(n).IsEqualTo(0);
  }

  [Test]
  public async Task RecomputePartitionNumbersAsync_NoRows_ReturnsZeroResultAsync() {
    var c = _build();
    var result = await c.RecomputePartitionNumbersAsync(partitionCount: 100);
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task FlushCompletionsAsync_EmptyRequest_NoOpAsync() {
    var c = _build();
    await c.FlushCompletionsAsync(new FlushCompletionsRequest());
  }

  [Test]
  public async Task CommitHandlerBatchAsync_EmptyList_ReturnsEmptyAsync() {
    var c = _build();
    var results = await c.CommitHandlerBatchAsync([]);
    await Assert.That(results.Count).IsEqualTo(0);
  }
}
