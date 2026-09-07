using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the split between leases and failures on <c>wh_perspective_events</c> (issue #700).
/// <c>attempts</c> counts dispatch starts: every claim, whether by <c>claim_work</c>,
/// <c>claim_orphaned_perspective_events</c> or the drain's own <c>get_stream_events</c>, bumps it.
/// That is the right diagnostic for "how many times has this row been handed to a worker", and it is
/// the wrong input for the dead-letter decision: a row whose lease lapses without an apply (the
/// worker discarded it, died mid-batch, or skipped it as cooled) is re-claimed and bumped again, so a
/// perfectly good event reaches the threshold without one apply ever failing. The decision must read
/// a counter that only a recorded failure moves.
/// </summary>
/// <docs>fundamentals/perspectives/perspective-worker</docs>
[Category("Shard3")]
public class PerspectiveFailureCounterSqlTests : EFCoreTestBase {

  [Test]
  public async Task LeasingViaGetStreamEvents_BumpsAttempts_NeverFailuresAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = (Guid)TrackedGuid.NewMedo();
    var (streamId, workId) = await _seedPendingRowAsync(conn);

    // Three leases of the same row, each lapsing before the next, none applied.
    for (var i = 0; i < 3; i++) {
      await _callGetStreamEventsAsync(conn, instance, streamId);
      await _lapseLeaseAsync(conn, workId);
    }

    var (attempts, failures) = await _readCountersAsync(conn, workId);
    await Assert.That(attempts).IsEqualTo(3).Because("each lease is a dispatch start");
    await Assert.That(failures).IsEqualTo(0).Because("no apply ran, so nothing failed");
  }

  [Test]
  public async Task RecordedFailure_BumpsFailures_AndLeavesAttemptsAloneAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = (Guid)TrackedGuid.NewMedo();
    var (streamId, workId) = await _seedPendingRowAsync(conn);
    await _callGetStreamEventsAsync(conn, instance, streamId);   // attempts = 1, leased

    await _recordFailureAsync(conn, workId, "apply threw");

    var (attempts, failures) = await _readCountersAsync(conn, workId);
    await Assert.That(failures).IsEqualTo(1).Because("a recorded failure is the only thing that moves the failure counter");
    await Assert.That(attempts).IsEqualTo(1).Because("recording a failure is not a new dispatch start (the next claim bumps attempts)");
  }

  [Test]
  public async Task GetStreamEvents_SurfacesFailures_ForTheDeadLetterDecisionAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = (Guid)TrackedGuid.NewMedo();
    var (streamId, workId) = await _seedPendingRowAsync(conn);
    await _setFailuresAsync(conn, workId, 4);

    var (outAttempts, outFailures) = await _callGetStreamEventsAsync(conn, instance, streamId);

    await Assert.That(outFailures).IsEqualTo(4).Because("the drain reads the failure counter off the fetched row");
    await Assert.That(outAttempts).IsEqualTo(1).Because("this fetch was the first lease");
  }

  // -------------------------------------------------------------------------------------------

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<(Guid StreamId, Guid WorkId)> _seedPendingRowAsync(NpgsqlConnection conn) {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags, created_at)
        VALUES (@eid, @sid, @sid, 'TestAggregate', 'TestNamespace.CounterEvent', 'null'::jsonb, 1, nextval('wh_commit_seq'), 0, NOW() - INTERVAL '1 minute')
        """;
      cmd.Parameters.AddWithValue("eid", eventId);
      cmd.Parameters.AddWithValue("sid", streamId);
      await cmd.ExecuteNonQueryAsync();
    }
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@eid, '{"Data":"x"}'::jsonb, '{}'::jsonb)
        """;
      cmd.Parameters.AddWithValue("eid", eventId);
      await cmd.ExecuteNonQueryAsync();
    }
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        INSERT INTO wh_perspective_events
          (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
        VALUES (@work, @sid, 'CounterPerspective', @eid, 1, 0, NOW() - INTERVAL '1 minute')
        """;
      cmd.Parameters.AddWithValue("work", workId);
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("eid", eventId);
      await cmd.ExecuteNonQueryAsync();
    }
    return (streamId, workId);
  }

  /// <summary>Calls the drain's fetch (which claims the row for the instance) and returns the surfaced counters.</summary>
  private static async Task<(int Attempts, int Failures)> _callGetStreamEventsAsync(NpgsqlConnection conn, Guid instance, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT out_attempts, out_failures FROM get_stream_events(@inst, @streams, NOW(), 300)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.Add(new NpgsqlParameter("streams", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { streamId } });
    await using var reader = await cmd.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue().Because("the seeded row is claimable and must be returned");
    return (reader.GetInt32(0), reader.GetInt32(1));
  }

  private static async Task _lapseLeaseAsync(NpgsqlConnection conn, Guid workId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_perspective_events SET lease_expiry = NOW() - INTERVAL '1 second' WHERE event_work_id = @work";
    cmd.Parameters.AddWithValue("work", workId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _recordFailureAsync(NpgsqlConnection conn, Guid workId, string error) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT process_perspective_event_failures(@failures::jsonb, NOW())";
    cmd.Parameters.AddWithValue("failures",
      $$"""[{"EventWorkId":"{{workId}}","CompletedStatus":0,"Error":"{{error}}","FailureReason":1}]""");
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _setFailuresAsync(NpgsqlConnection conn, Guid workId, int failures) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_perspective_events SET failures = @n WHERE event_work_id = @work";
    cmd.Parameters.AddWithValue("n", failures);
    cmd.Parameters.AddWithValue("work", workId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<(int Attempts, int Failures)> _readCountersAsync(NpgsqlConnection conn, Guid workId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT attempts, failures FROM wh_perspective_events WHERE event_work_id = @work";
    cmd.Parameters.AddWithValue("work", workId);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt32(0), reader.GetInt32(1));
  }
}
