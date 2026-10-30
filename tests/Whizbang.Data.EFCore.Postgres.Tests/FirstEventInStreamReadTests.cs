// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Every read a perspective folds from says which event is the first in its stream (#1878): the rewind and rebuild
/// read (<c>ReadPolymorphicAsync</c>) and the live drain (<c>get_stream_events</c>, then <c>DeserializeStreamEvents</c>).
/// The answer comes from the event's position: version 1, or version 0 for a stream whose numbering began there. A
/// version-1 event of a stream that also holds a version 0 is not first, even when the read never returns the version 0.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
[Category("Integration")]
[Category("Shard5")]
public class FirstEventInStreamReadTests : EFCoreTestBase {
  private static readonly Type[] _probeTypes = [typeof(ReadProbeContracts.ProbeAlphaEvent)];
  private static readonly bool[] _firstOfThree = [true, false, false];
  private static readonly bool[] _noneOfTwo = [false, false];
  private static readonly bool[] _firstOfTwo = [true, false];

  [Test]
  [Arguments(0)]
  [Arguments(1)]
  public async Task ReadPolymorphicAsync_MarksOnlyTheEventAtTheStreamsFirstPositionAsFirstAsync(int firstVersion) {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = (Guid)TrackedGuid.New();
    var ids = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      ids.Add(await _appendAsync(store, streamId, i));
    }
    if (firstVersion == 1) {
      await _renumberFromOneAsync(context, streamId);
    }

    var whole = await _readAsync(store, streamId, fromEventId: null);
    var afterFirst = await _readAsync(store, streamId, fromEventId: ids[0]);

    await Assert.That(whole.Select(e => e.LocalStreamVersion)).IsEquivalentTo(
      new long?[] { firstVersion, firstVersion + 1, firstVersion + 2 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(whole.Select(e => e.IsFirstInLocalStream)).IsEquivalentTo(_firstOfThree, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(afterFirst.Select(e => e.IsFirstInLocalStream)).IsEquivalentTo(_noneOfTwo, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a version-1 event of a stream that also holds a version 0 is not first, read alone or not");
  }

  [Test]
  public async Task GetStreamEvents_ReturnsWhetherEachEventIsTheFirstInItsStreamAsync() {
    await using var context = CreateDbContext();
    var conn = await _openAsync(context);
    var instanceId = (Guid)TrackedGuid.New();
    await _registerInstanceAsync(conn, instanceId);
    var olderStream = (Guid)TrackedGuid.New();
    var newerStream = (Guid)TrackedGuid.New();
    // A stream numbered from 0 (written before numbering was unified) and one numbered from 1.
    var olderZero = await _seedLeasedEventAsync(conn, instanceId, olderStream, version: 0);
    var olderOne = await _seedLeasedEventAsync(conn, instanceId, olderStream, version: 1);
    var newerOne = await _seedLeasedEventAsync(conn, instanceId, newerStream, version: 1);
    var newerTwo = await _seedLeasedEventAsync(conn, instanceId, newerStream, version: 2);
    await _stampAsync(conn);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());

    var rows = await coordinator.GetStreamEventsAsync(instanceId, [olderStream, newerStream]);

    var first = rows.ToDictionary(r => r.EventId, r => r.IsFirstInStream);
    await Assert.That(first.Count).IsEqualTo(4);
    await Assert.That(first[olderZero]).IsTrue();
    await Assert.That(first[olderOne]).IsFalse()
      .Because("version 1 is not first in a stream that also holds a version 0");
    await Assert.That(first[newerOne]).IsTrue()
      .Because("version 1 is first in a stream numbered from 1");
    await Assert.That(first[newerTwo]).IsFalse();
  }

  [Test]
  public async Task DeserializeStreamEvents_CarriesTheFirstInStreamFactOntoTheEnvelopeAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());
    var streamId = (Guid)TrackedGuid.New();
    var rows = new[] { _streamEvent(streamId, isFirst: true), _streamEvent(streamId, isFirst: false) };

    var envelopes = store.DeserializeStreamEvents(rows, _probeTypes);

    await Assert.That(envelopes.Select(e => e.IsFirstInLocalStream)).IsEquivalentTo(
      _firstOfTwo, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the live drain hands the runner the fact the drain query read");
  }

  // ── helpers ─────────────────────────────────────────────────────────────────────────────────

  private static StreamEventData _streamEvent(Guid streamId, bool isFirst) {
    var payload = new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 0 };
    return new StreamEventData {
      StreamId = streamId,
      EventId = (Guid)TrackedGuid.New(),
      EventType = TypeNameFormatter.Format(typeof(ReadProbeContracts.ProbeAlphaEvent)),
      EventData = JsonSerializer.Serialize(payload, JsonContextRegistry.CreateCombinedOptions()),
      EventWorkId = (Guid)TrackedGuid.New(),
      IsFirstInStream = isFirst,
    };
  }

  private static async Task<Guid> _appendAsync(EFCoreEventStore<WorkCoordinationDbContext> store, Guid streamId, int ordinal) {
    var messageId = MessageId.New();
    await store.AppendAsync(streamId, new MessageEnvelope<ReadProbeContracts.ProbeAlphaEvent> {
      MessageId = messageId,
      Payload = new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = ordinal },
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UnixEpoch, ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    });
    return messageId.Value;
  }

  private static async Task<List<MessageEnvelope<IEvent>>> _readAsync(
      EFCoreEventStore<WorkCoordinationDbContext> store, Guid streamId, Guid? fromEventId) {
    var read = new List<MessageEnvelope<IEvent>>();
    await foreach (var envelope in store.ReadPolymorphicAsync(streamId, fromEventId, _probeTypes, CancellationToken.None)) {
      read.Add(envelope);
    }
    return read;
  }

  /// <summary>Moves a stream a direct append numbered from 0 onto the numbering the dispatch paths use, from 1.</summary>
  private static async Task _renumberFromOneAsync(WorkCoordinationDbContext context, Guid streamId) {
    var conn = await _openAsync(context);
    await using var cmd = conn.CreateCommand();
    // Two steps, so no intermediate version collides with one not yet moved.
    cmd.CommandText = """
      UPDATE wh_event_store SET version = version + 1000 WHERE stream_id = @sid;
      UPDATE wh_event_store SET version = version - 999 WHERE stream_id = @sid;
      """;
    cmd.Parameters.AddWithValue("sid", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext context) {
    var conn = (NpgsqlConnection)context.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<Guid> _seedLeasedEventAsync(NpgsqlConnection conn, Guid instanceId, Guid streamId, int version) {
    var eventId = (Guid)TrackedGuid.New();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
      VALUES (@eid, @sid, @sid, 'TestAggregate', @ver, 'TestEvent', NULL, NOW());
      INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES (@eid, '{}'::jsonb, '{}'::jsonb);
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, created_at)
      VALUES (@work, @sid, 'Projection.FirstInStream', @eid, @inst, NOW() + INTERVAL '5 minutes', 0, 0, 0, NOW());
      """;
    cmd.Parameters.AddWithValue("eid", eventId);
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("ver", version);
    cmd.Parameters.AddWithValue("work", (Guid)TrackedGuid.New());
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
    return eventId;
  }

  private static async Task _stampAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stamp_pending_commit_sequences(1000)";
    await cmd.ExecuteScalarAsync();
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = EXCLUDED.last_heartbeat_at
      """;
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }
}
