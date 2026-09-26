using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Integration tests for <see cref="EFCorePerspectiveReplayReader{TDbContext}"/>
/// running against a real PostgreSQL database. Verifies that events are returned
/// with IsNew correctly set per row in the wh_perspective_events work queue:
/// row present (and not yet processed) → IsNew=true; row absent → IsNew=false.
/// </summary>
[Category("Shard3")]
public class EFCorePerspectiveReplayReaderTests : EFCoreTestBase {

  private const string PerspectiveName = "replay_reader_test";

  private async Task<List<MessageEnvelope<IEvent>>> _appendEventsAsync(Guid streamId, int count) {
    await using var dbContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(dbContext);
    var list = new List<MessageEnvelope<IEvent>>(count);
    for (var i = 0; i < count; i++) {
      var envelope = new MessageEnvelope<ActionTestCreatedEvent> {
        MessageId = MessageId.From(TrackedGuid.New()),
        Payload = new ActionTestCreatedEvent { StreamId = streamId, Name = $"evt-{i}", Value = i },
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      await eventStore.AppendAsync(streamId, envelope);
      list.Add(new MessageEnvelope<IEvent> {
        MessageId = envelope.MessageId,
        Payload = envelope.Payload,
        Hops = envelope.Hops,
        DispatchContext = envelope.DispatchContext
      });
    }
    await dbContext.SaveChangesAsync();
    return list;
  }

  private async Task _insertPendingPerspectiveEventsAsync(Guid streamId, IEnumerable<Guid> eventIds) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, status, attempts)
      VALUES
        (gen_random_uuid(), @stream_id, @perspective_name, @event_id, 1, 0)
      ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;
      """;
    var streamParam = cmd.CreateParameter();
    streamParam.ParameterName = "@stream_id";
    streamParam.Value = streamId;
    cmd.Parameters.Add(streamParam);

    var perspParam = cmd.CreateParameter();
    perspParam.ParameterName = "@perspective_name";
    perspParam.Value = PerspectiveName;
    cmd.Parameters.Add(perspParam);

    var eventParam = cmd.CreateParameter();
    eventParam.ParameterName = "@event_id";
    cmd.Parameters.Add(eventParam);

    foreach (var id in eventIds) {
      eventParam.Value = id;
      await cmd.ExecuteNonQueryAsync();
    }
  }

  [Test]
  public async Task ReadReplayEventsAsync_EventsInWorkQueue_AnnotateIsNewTrue_Async() {
    var streamId = Guid.NewGuid();
    var events = await _appendEventsAsync(streamId, count: 4);
    // Events 0 and 2 are pending (still in wh_perspective_events)
    await _insertPendingPerspectiveEventsAsync(streamId, [events[0].MessageId.Value, events[2].MessageId.Value]);

    await using var dbContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(dbContext);
    var reader = new EFCorePerspectiveReplayReader<WorkCoordinationDbContext>(dbContext, eventStore);

    var results = new List<ReplayEventEnvelope>();
    await foreach (var env in reader.ReadReplayEventsAsync(
        streamId, PerspectiveName, fromVersionExclusive: 0,
        [typeof(ActionTestCreatedEvent)], CancellationToken.None)) {
      results.Add(env);
    }

    await Assert.That(results.Count).IsEqualTo(4)
      .Because("All 4 events in the stream must be returned by the reader.");

    var byId = results.ToDictionary(r => r.Envelope.MessageId.Value, r => r.IsNew);
    await Assert.That(byId[events[0].MessageId.Value]).IsTrue()
      .Because("Event 0 has a pending row in wh_perspective_events → IsNew=true.");
    await Assert.That(byId[events[1].MessageId.Value]).IsFalse()
      .Because("Event 1 has no pending row → IsNew=false (already processed / never queued).");
    await Assert.That(byId[events[2].MessageId.Value]).IsTrue()
      .Because("Event 2 has a pending row → IsNew=true.");
    await Assert.That(byId[events[3].MessageId.Value]).IsFalse()
      .Because("Event 3 has no pending row → IsNew=false.");
  }

  [Test]
  public async Task ReadReplayEventsAsync_WhenThePendingIdReadFails_StillClosesTheConnectionItOpenedAsync() {
    // The reader opens the DbContext's connection itself when it finds it closed, and owes it a
    // close on the way out. Replay runs on the perspective worker, which retries a failing stream
    // for as long as it keeps failing — so a connection leaked on the failure path is leaked once
    // per attempt. The pool empties, and the first symptom is unrelated work on the same context
    // timing out waiting for a connection, which points investigation everywhere but here.
    var streamId = Guid.NewGuid();

    // A real fault rather than a mocked one: the pending-id query raises 42P01 against the actual
    // Npgsql command the try block wraps. EFCoreTestBase gives this test its own database.
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await using var drop = conn.CreateCommand();
      drop.CommandText = "DROP TABLE wh_perspective_events CASCADE";
      await drop.ExecuteNonQueryAsync();
    }

    await using var dbContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(dbContext);
    var connection = dbContext.Database.GetDbConnection();
    await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Closed)
      .Because("the reader only owes a close for a connection it opened itself — if this were "
             + "already open the test would assert nothing about the finally");

    var reader = new EFCorePerspectiveReplayReader<WorkCoordinationDbContext>(dbContext, eventStore);
    var enumerator = reader.ReadReplayEventsAsync(
      streamId, PerspectiveName, fromVersionExclusive: 0,
      [typeof(ActionTestCreatedEvent)], CancellationToken.None).GetAsyncEnumerator(CancellationToken.None);

    Exception? caught = null;
    try {
      await enumerator.MoveNextAsync();
    } catch (Exception ex) {
      caught = ex;
    } finally {
      await enumerator.DisposeAsync();
    }

    await Assert.That(caught).IsNotNull()
      .Because("the failure must reach the caller — swallowing it would report a stream as "
             + "replayed with every event marked already-completed");
    await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Closed)
      .Because("the connection the reader opened has to be given back even when the read throws; "
             + "a repeatedly failing replay would otherwise drain the pool one attempt at a time");
  }

  [Test]
  public async Task ReadReplayEventsAsync_EmptyWorkQueue_AllIsNewFalse_Async() {
    var streamId = Guid.NewGuid();
    _ = await _appendEventsAsync(streamId, count: 3);
    // No rows inserted into wh_perspective_events — everything already completed.

    await using var dbContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(dbContext);
    var reader = new EFCorePerspectiveReplayReader<WorkCoordinationDbContext>(dbContext, eventStore);

    var results = new List<ReplayEventEnvelope>();
    await foreach (var env in reader.ReadReplayEventsAsync(
        streamId, PerspectiveName, fromVersionExclusive: 0,
        [typeof(ActionTestCreatedEvent)], CancellationToken.None)) {
      results.Add(env);
    }

    await Assert.That(results.Count).IsEqualTo(3);
    await Assert.That(results.All(r => !r.IsNew)).IsTrue()
      .Because("LOCK-IN: With no pending work-queue rows, every event must be annotated IsNew=false.");
  }

  [Test]
  public async Task ReadReplayEventsAsync_PerspectiveNameFilter_OnlyMatchingRowsConsidered_Async() {
    // Events pending for a DIFFERENT perspective must not leak into this perspective's is_new set.
    var streamId = Guid.NewGuid();
    var events = await _appendEventsAsync(streamId, count: 2);
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, status, attempts)
      VALUES
        (gen_random_uuid(), @stream_id, 'other_perspective', @event_id, 1, 0)
      ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;
      """;
    var streamParam = cmd.CreateParameter();
    streamParam.ParameterName = "@stream_id";
    streamParam.Value = streamId;
    cmd.Parameters.Add(streamParam);
    var eventParam = cmd.CreateParameter();
    eventParam.ParameterName = "@event_id";
    eventParam.Value = events[0].MessageId.Value;
    cmd.Parameters.Add(eventParam);
    await cmd.ExecuteNonQueryAsync();

    await using var dbContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(dbContext);
    var reader = new EFCorePerspectiveReplayReader<WorkCoordinationDbContext>(dbContext, eventStore);

    var results = new List<ReplayEventEnvelope>();
    await foreach (var env in reader.ReadReplayEventsAsync(
        streamId, PerspectiveName, fromVersionExclusive: 0,
        [typeof(ActionTestCreatedEvent)], CancellationToken.None)) {
      results.Add(env);
    }

    await Assert.That(results.All(r => !r.IsNew)).IsTrue()
      .Because("LOCK-IN: wh_perspective_events rows for OTHER perspectives must not mark our events as IsNew.");
  }

  /// <summary>
  /// When the context's connection is ALREADY open, the lookup is a borrower: it must leave the
  /// connection exactly as it found it. Closing a connection the caller owns breaks whatever the
  /// caller was in the middle of — a replay runs inside a scope that also writes the perspective
  /// rows, and on Npgsql an explicit transaction lives on the connection, so closing it under an
  /// open transaction discards work the caller believes is still pending.
  /// </summary>
  [Test]
  public async Task ReadReplayEventsAsync_ConnectionAlreadyOpen_LeavesItOpenForTheCallerAsync() {
    var streamId = Guid.NewGuid();
    var events = await _appendEventsAsync(streamId, count: 2);
    await _insertPendingPerspectiveEventsAsync(streamId, [events[0].MessageId.Value]);

    await using var dbContext = CreateDbContext();
    var connection = dbContext.Database.GetDbConnection();
    await connection.OpenAsync();
    await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Open);

    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(dbContext);
    var reader = new EFCorePerspectiveReplayReader<WorkCoordinationDbContext>(dbContext, eventStore);

    var results = new List<ReplayEventEnvelope>();
    await foreach (var env in reader.ReadReplayEventsAsync(
        streamId, PerspectiveName, fromVersionExclusive: 0,
        [typeof(ActionTestCreatedEvent)], CancellationToken.None)) {
      results.Add(env);
    }

    await Assert.That(results.Count).IsEqualTo(2)
      .Because("the read still has to work on a borrowed connection, or the leave-it-alone rule is vacuous");
    await Assert.That(results[0].IsNew).IsTrue()
      .Because("the pending row was read over the caller's own connection");
    await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Open)
      .Because("the reader did not open this connection, so it must not close it");
  }

  /// <summary>
  /// The pending-id lookup borrows the context's connection and opens it when it finds it closed,
  /// so it owes the context a closed connection back — including when the query itself fails. A
  /// replay runs on a scoped context that outlives this call; leaving the connection open on the
  /// failure path holds a backend for the rest of that scope, and the failures this path sees are
  /// exactly the ones that repeat (a missing table, a revoked grant), so one leak per retry
  /// becomes a pool exhausted by a condition that is not even about connections.
  /// </summary>
  [Test]
  public async Task ReadReplayEventsAsync_PendingLookupFails_HandsBackAClosedConnectionAndSurfacesTheErrorAsync() {
    var streamId = Guid.NewGuid();
    _ = await _appendEventsAsync(streamId, count: 1);

    // Take the work-queue table away: the pending-id query is the first thing the read does, so
    // it fails after the connection has been opened and before anything else runs.
    await using (var admin = new NpgsqlConnection(ConnectionString)) {
      await admin.OpenAsync();
      await using var drop = new NpgsqlCommand("DROP TABLE wh_perspective_events", admin);
      await drop.ExecuteNonQueryAsync();
    }

    await using var dbContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(dbContext);
    var reader = new EFCorePerspectiveReplayReader<WorkCoordinationDbContext>(dbContext, eventStore);
    var connection = dbContext.Database.GetDbConnection();
    await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Closed)
      .Because("the reader must be the one that opens it for the close-on-failure contract to mean anything");

    await Assert.That(async () => {
      await foreach (var _ in reader.ReadReplayEventsAsync(
          streamId, PerspectiveName, fromVersionExclusive: 0,
          [typeof(ActionTestCreatedEvent)], CancellationToken.None)) {
        // The first MoveNext performs the pending-id lookup; nothing is ever yielded.
      }
    }).Throws<PostgresException>()
      .Because("a replay that cannot tell new events from replayed ones must fail loudly, not silently treat everything as already applied");

    await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Closed)
      .Because("the connection the lookup opened has to go back closed even when the lookup threw");
  }
}
