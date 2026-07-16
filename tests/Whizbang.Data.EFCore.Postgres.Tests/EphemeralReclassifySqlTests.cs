using System;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for migration 074's reclassification primitive
/// (<c>reclassify_events_ephemeral</c>, E1 #13c1). When a type that already has stored Sourced events is
/// made ephemeral, this retroactively stamps <c>EventFlags.Ephemeral</c> on its historical rows and
/// offloads their inline bodies to <c>wh_event_body</c> — exactly what the emit chain would have done at
/// store time — so the 073 reaper then cleans them up consumption-gated. A homogeneity guard skips and
/// reports any stream that would become mixed (contains the target type AND a Sourced event of another
/// type), so the all-Sourced-or-all-Ephemeral invariant is never violated. Verified against a real Postgres.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class EphemeralReclassifySqlTests : EFCoreTestBase {
  private static string _commitRequest(Guid instanceId, Guid eventId, Guid streamId, string eventType, int flags) => $$"""
    {
      "instance_id": "{{instanceId}}",
      "service_name": "test",
      "host_name": "test-host",
      "process_id": 1,
      "new_outbox_messages": [{
        "MessageId": "{{eventId}}",
        "Destination": "out-topic",
        "MessageType": "{{eventType}}",
        "EnvelopeType": null,
        "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": true,
        "Flags": {{flags}}
      }]
    }
    """;

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _commitAsync(NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType, int flags) {
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", _commitRequest(Guid.NewGuid(), eventId, streamId, eventType, flags));
    _ = await call.ExecuteScalarAsync();
  }

  private static async Task<(long reclassified, long streams, long blocked)> _reclassifyAsync(NpgsqlConnection connection, params string[] eventTypes) {
    await using var r = connection.CreateCommand();
    r.CommandText = "SELECT events_reclassified, streams_reclassified, streams_blocked FROM reclassify_events_ephemeral(@t)";
    r.Parameters.AddWithValue("t", eventTypes);
    await using var rd = await r.ExecuteReaderAsync();
    await rd.ReadAsync();
    return (rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2));
  }

  private static async Task<(int flags, bool inlineNull, long bodyCount)> _rowStateAsync(NpgsqlConnection connection, Guid eventId) {
    await using var v = connection.CreateCommand();
    v.CommandText = @"
      SELECT es.flags, (es.event_data IS NULL),
             (SELECT count(*) FROM wh_event_body eb WHERE eb.event_id = es.event_id)
      FROM wh_event_store es WHERE es.event_id = @id";
    v.Parameters.AddWithValue("id", eventId);
    await using var rd = await v.ExecuteReaderAsync();
    await rd.ReadAsync();
    return (rd.GetInt32(0), rd.GetBoolean(1), rd.GetInt64(2));
  }

  [Test]
  public async Task Reclassify_HistoricalSourcedEvent_BecomesEphemeral_BodyOffloadedAndReapableAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var eventId = Guid.NewGuid();
    const string eventType = "Whizbang.Tests.WasSourcedNowEphemeralEvent";
    // Stored while the type was Sourced: flags 0, body inline, nothing in wh_event_body.
    await _commitAsync(connection, eventId, Guid.NewGuid(), eventType, flags: 0);
    var before = await _rowStateAsync(connection, eventId);
    await Assert.That(before.flags & 8).IsEqualTo(0).Because("It was stored as a Sourced event.");
    await Assert.That(before.inlineNull).IsFalse().Because("A Sourced event keeps its body inline.");
    await Assert.That(before.bodyCount).IsEqualTo(0L).Because("Nothing was offloaded at store time.");

    // The type is now [Ephemeral]; reclassify its history.
    var (reclassified, streams, blocked) = await _reclassifyAsync(connection, eventType);
    await Assert.That(reclassified).IsEqualTo(1L).Because("The one historical event of the type is reclassified.");
    await Assert.That(streams).IsEqualTo(1L).Because("Its single stream is fully reclassified.");
    await Assert.That(blocked).IsEqualTo(0L).Because("A homogeneous stream is never blocked.");

    var after = await _rowStateAsync(connection, eventId);
    await Assert.That(after.flags & 8).IsEqualTo(8).Because("The historical event is now stamped ephemeral.");
    await Assert.That(after.inlineNull).IsTrue().Because("Its inline body was moved out.");
    await Assert.That(after.bodyCount).IsEqualTo(1L).Because("The body was offloaded to wh_event_body — as if emitted ephemeral.");

    // Offloaded body carries the real payload …
    await using (var b = connection.CreateCommand()) {
      b.CommandText = "SELECT (event_data->>'OrderId') FROM wh_event_body WHERE event_id = @id";
      b.Parameters.AddWithValue("id", eventId);
      await Assert.That((string?)await b.ExecuteScalarAsync()).IsEqualTo("42").Because("The offloaded body is the full event data.");
    }

    // … and the existing reaper cleans it up (no consuming perspective => reapable at once).
    await using (var m = connection.CreateCommand()) {
      m.CommandText = "SELECT * FROM perform_maintenance()";
      await using var rd = await m.ExecuteReaderAsync();
      while (await rd.ReadAsync()) { }
    }
    await Assert.That((await _rowStateAsync(connection, eventId)).bodyCount).IsEqualTo(0L)
      .Because("After reclassification the tier-1 reaper reaps the now-ephemeral body, consumption-gated.");
  }

  [Test]
  public async Task Reclassify_MixedStream_SkipsAndReportsBlocked_ButReclassifiesCleanStreamAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    const string targetType = "Whizbang.Tests.TargetEphemeralEvent";

    // Clean stream: only the target type.
    var cleanStream = Guid.NewGuid();
    var cleanEvent = Guid.NewGuid();
    await _commitAsync(connection, cleanEvent, cleanStream, targetType, flags: 0);

    // Mixed stream: the target type AND a Sourced event of a different type.
    var mixedStream = Guid.NewGuid();
    var mixedTargetEvent = Guid.NewGuid();
    await _commitAsync(connection, mixedTargetEvent, mixedStream, targetType, flags: 0);
    await _commitAsync(connection, Guid.NewGuid(), mixedStream, "Whizbang.Tests.StaysSourcedEvent", flags: 0);

    var (reclassified, streams, blocked) = await _reclassifyAsync(connection, targetType);
    await Assert.That(reclassified).IsEqualTo(1L).Because("Only the clean stream's target event is reclassified.");
    await Assert.That(streams).IsEqualTo(1L).Because("Only the homogeneous stream is reclassified.");
    await Assert.That(blocked).IsEqualTo(1L).Because("The mixed stream is reported as a blocker, not silently mixed.");

    await Assert.That((await _rowStateAsync(connection, cleanEvent)).flags & 8).IsEqualTo(8)
      .Because("The clean stream's target event is reclassified.");
    await Assert.That((await _rowStateAsync(connection, mixedTargetEvent)).flags & 8).IsEqualTo(0)
      .Because("The mixed stream's target event is left Sourced — reclassifying it would violate the homogeneous-stream invariant.");
  }

  [Test]
  public async Task Reclassify_Idempotent_SecondRunReclassifiesZeroAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    const string eventType = "Whizbang.Tests.IdempotentReclassifyEvent";
    await _commitAsync(connection, Guid.NewGuid(), Guid.NewGuid(), eventType, flags: 0);

    var first = await _reclassifyAsync(connection, eventType);
    await Assert.That(first.reclassified).IsEqualTo(1L).Because("First run reclassifies the historical event.");

    var second = await _reclassifyAsync(connection, eventType);
    await Assert.That(second.reclassified).IsEqualTo(0L).Because("Re-running finds nothing left to reclassify — idempotent.");
    await Assert.That(second.blocked).IsEqualTo(0L).Because("No blockers on a re-run either.");
  }

  [Test]
  public async Task Reclassify_AlreadyEphemeralEvent_LeftAloneAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var eventId = Guid.NewGuid();
    const string eventType = "Whizbang.Tests.BornEphemeralEvent";
    // Emitted ephemeral from the start: flags 8, body already offloaded.
    await _commitAsync(connection, eventId, Guid.NewGuid(), eventType, flags: 8);
    var before = await _rowStateAsync(connection, eventId);
    await Assert.That(before.bodyCount).IsEqualTo(1L).Because("Born-ephemeral event already has its body in wh_event_body.");

    var (reclassified, _, blocked) = await _reclassifyAsync(connection, eventType);
    await Assert.That(reclassified).IsEqualTo(0L).Because("An already-ephemeral event needs no reclassification.");
    await Assert.That(blocked).IsEqualTo(0L).Because("It is not a blocker.");

    var after = await _rowStateAsync(connection, eventId);
    await Assert.That(after.flags & 8).IsEqualTo(8).Because("Still ephemeral.");
    await Assert.That(after.bodyCount).IsEqualTo(1L).Because("Its offloaded body is untouched — no double-processing.");
  }

  [Test]
  public async Task Reclassify_RenamedType_MatchesUnderBothNames_WithoutFalseMixedBlockAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    // A type that was renamed: some history is stored under the former name, some under the current name,
    // in the SAME stream. Reclassifying with the full name set (current + former) must catch BOTH and must
    // NOT treat the former-name events as "another type" (which would falsely block the stream).
    const string currentName = "Whizbang.Tests.RenamedEphemeralEventV2";
    const string formerName = "Whizbang.Tests.RenamedEphemeralEventV1";
    var stream = Guid.NewGuid();
    var underFormer = Guid.NewGuid();
    var underCurrent = Guid.NewGuid();
    await _commitAsync(connection, underFormer, stream, formerName, flags: 0);
    await _commitAsync(connection, underCurrent, stream, currentName, flags: 0);

    var (reclassified, streams, blocked) = await _reclassifyAsync(connection, currentName, formerName);
    await Assert.That(reclassified).IsEqualTo(2L).Because("Both the former-name and current-name events are the same logical type — both reclassify.");
    await Assert.That(streams).IsEqualTo(1L).Because("They share one stream.");
    await Assert.That(blocked).IsEqualTo(0L).Because("A type's own former-name events must not be mistaken for 'another type' — the stream stays homogeneous.");

    await Assert.That((await _rowStateAsync(connection, underFormer)).flags & 8).IsEqualTo(8).Because("Former-name history is reclassified.");
    await Assert.That((await _rowStateAsync(connection, underCurrent)).flags & 8).IsEqualTo(8).Because("Current-name history is reclassified.");
  }
}
