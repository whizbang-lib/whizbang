using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Phase B integration tests for handler-commit methods on <see cref="IWorkCoordinator"/>:
/// <see cref="IWorkCoordinator.CommitHandlerResultAsync"/> and
/// <see cref="IWorkCoordinator.CommitHandlerBatchAsync"/>.
/// The latter is the throughput multiplier (SAVEPOINT-per-handler isolation).
/// </summary>
/// <docs>fundamentals/work-coordinator/handler-commit</docs>
[Category("Shard4")]
public class EFCoreCommitHandlerTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> Coord(WorkCoordinationDbContext ctx) =>
    new(ctx, JsonContextRegistry.CreateCombinedOptions());

  private static OutboxMessage MakeOutbox(Guid messageId, Guid streamId) =>
    CreateTestOutboxMessage(messageId, "out-topic", streamId);

  [Test]
  public async Task CommitHandlerResultAsync_HappyPath_MarksInboxAndStoresOutboxAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var inboxId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var emittedId = Guid.CreateVersion7();

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, 0, NOW(),
                @inst, NOW() + INTERVAL '60 seconds', @stream, 0)";
      ins.Parameters.AddWithValue("msg", inboxId);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    await coordinator.CommitHandlerResultAsync(new HandlerCommitRequest(
      HandlerId: Guid.NewGuid(),
      InstanceId: instanceId,
      ServiceName: "test",
      HostName: "test-host",
      ProcessId: 1,
      PartitionCount: 10000,
      InboxCompletion: new HandlerInboxCompletion(inboxId, Status: 4),
      NewOutboxMessages: [MakeOutbox(emittedId, streamId)]));

    await using (var verify = conn.CreateCommand()) {
      verify.CommandText = "SELECT processed_at IS NOT NULL FROM wh_inbox WHERE message_id = @msg";
      verify.Parameters.AddWithValue("msg", inboxId);
      await Assert.That((bool)(await verify.ExecuteScalarAsync())!).IsTrue();
    }
    await using (var verify = conn.CreateCommand()) {
      verify.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = @msg";
      verify.Parameters.AddWithValue("msg", emittedId);
      await Assert.That((long)(await verify.ExecuteScalarAsync())!).IsEqualTo(1L);
    }
  }

  [Test]
  public async Task CommitHandlerBatchAsync_AllSucceed_ReturnsAllSuccessAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var inboxIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    foreach (var id in inboxIds) {
      await using var ins = conn.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, 0, NOW(),
                @inst, NOW() + INTERVAL '60 seconds', @stream, 0)";
      ins.Parameters.AddWithValue("msg", id);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    var requests = inboxIds.Select(id => new HandlerCommitRequest(
      HandlerId: Guid.NewGuid(),
      InstanceId: instanceId,
      ServiceName: "test",
      HostName: "test-host",
      ProcessId: 1,
      PartitionCount: 10000,
      InboxCompletion: new HandlerInboxCompletion(id, Status: 4))).ToList();

    var results = await coordinator.CommitHandlerBatchAsync(requests);

    await Assert.That(results.Count).IsEqualTo(3);
    await Assert.That(results.All(r => r.Success)).IsTrue();
    await Assert.That(results.All(r => r.ErrorMessage == null)).IsTrue();
  }

  [Test]
  public async Task CommitHandlerBatchAsync_EmptyList_ReturnsEmptyResultAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var results = await coordinator.CommitHandlerBatchAsync([]);
    await Assert.That(results.Count).IsEqualTo(0);
  }

  private sealed class _captureLogger : Microsoft.Extensions.Logging.ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>> {
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (Entries) { Entries.Add((logLevel, formatter(state, exception))); }
    }
  }

  [Test]
  public async Task CommitHandlerBatchAsync_TierOneFallback_LogsReasonAndCountsAsync() {
    // #573: the orchestrator's silent fallback made 930k per-handler commits look healthy.
    // The caller now surfaces the tier: a WARNING with the Tier-1 SQLSTATE and a counter
    // an operator can alert on. Fault injection: a test-owned trigger raises on a sentinel
    // outbox destination — the raise escapes the bulk tier whole; the savepoint loop then
    // isolates it to the one poisoned handler.
    var logger = new _captureLogger();
    var metrics = new Whizbang.Core.Observability.WorkCoordinatorMetrics(new Whizbang.Core.Observability.WhizbangMetrics());
    long fallbacks = 0;
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, metrics.ProcessBatchCalls.Meter)
          && instrument.Name == "whizbang.work_coordinator.commit_handler.fallbacks") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref fallbacks, value));
    listener.Start();

    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions(), logger, metrics);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }

    await using (var trg = conn.CreateCommand()) {
      trg.CommandText = @"
        CREATE OR REPLACE FUNCTION test_boom() RETURNS trigger LANGUAGE plpgsql AS
        $$ BEGIN IF NEW.destination = 'boom-topic' THEN RAISE EXCEPTION 'boom'; END IF; RETURN NEW; END; $$;
        CREATE TRIGGER test_boom_trigger BEFORE INSERT ON wh_outbox FOR EACH ROW EXECUTE FUNCTION test_boom();";
      await trg.ExecuteNonQueryAsync();
    }
    var instanceId = Guid.NewGuid();
    var inboxIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
    foreach (var id in inboxIds) {
      await using var ins = conn.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, 0, NOW(),
                @inst, NOW() + INTERVAL '60 seconds', @stream, 0)";
      ins.Parameters.AddWithValue("msg", id);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }
    var streamId = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    HandlerCommitRequest req(Guid inboxId, string destination) => new(
      HandlerId: Guid.NewGuid(), InstanceId: instanceId, ServiceName: "test", HostName: "test-host",
      ProcessId: 1, PartitionCount: 10000,
      InboxCompletion: new HandlerInboxCompletion(inboxId, Status: 4),
      NewOutboxMessages: [CreateTestOutboxMessage((Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo(), destination, streamId)]);

    var results = await coordinator.CommitHandlerBatchAsync(
      [req(inboxIds[0], "ok-topic"), req(inboxIds[1], "boom-topic")]);

    await Assert.That(results.Count).IsEqualTo(2);
    await Assert.That(results.Count(r => r.Success)).IsEqualTo(1)
      .Because("the savepoint loop isolates the duplicate to the second handler");
    List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> entries;
    lock (logger.Entries) { entries = [.. logger.Entries]; }
    await Assert.That(entries.Any(e =>
        e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("P0001"))).IsTrue()
      .Because("the Tier-1 SQLSTATE is the diagnosis — discarding it left the slow path "
             + "undiagnosable for the life of a deployment");
    await Assert.That(Interlocked.Read(ref fallbacks)).IsEqualTo(1L)
      .Because("one batch fell back once — the counter is what an operator alerts on when "
             + "a fleet quietly lives on the slow path");
  }

}
