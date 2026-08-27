using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The bucketed drain plan, issued against real Postgres.
///
/// <para>
/// The drain allocates a global row budget per stream, but a fetch takes ONE cap for every stream
/// in the call. The plan therefore quantizes allocations and issues one fetch per distinct cap.
/// The unit tests pin the arithmetic; what they cannot show is that the resulting CALL PATTERN
/// actually drains a real store correctly — that a wide-cap call returns a deep stream in one trip
/// while a floor-cap call still bounds a shallow one, and that streams sharing a call do not
/// interfere.
/// </para>
///
/// <para>
/// This is the invariant the whole change rests on: a deep stream previously took ceiling/floor
/// round-trips because every fetch was capped at the floor, and the fix is worth nothing if the
/// wider cap does not actually come back with the rows.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
[Category("Integration")]
[Category("Shard3")]
public class InboxFetchBucketedPlanSqlTests : EFCoreTestBase {

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _seedAsync(NpgsqlConnection conn, Guid streamId, Guid messageId, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata,
                            stream_id, is_event, status, attempts, instance_id, lease_expiry,
                            source_service_id)
      VALUES (@id, 'h', 'T, A', '{"p":"x"}'::jsonb, '{}'::jsonb,
              @stream, true, 1, 0, @inst, NOW() + interval '5 minutes',
              '00000000-0000-0000-0000-000000000001')
      """;
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static Guid _msg(int stream, int n) => Guid.Parse($"019f{stream:D4}-0000-7000-8000-{n:D12}");

  [Test]
  public async Task AWideCapDrainsADeepStreamInOneTripWhereTheFloorNeedsManyAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var deep = Guid.Parse("0c0c0c0c-1111-4111-8111-111111111111");
    var instance = Guid.Parse("0d0d0d0d-1111-4111-8111-111111111111");

    for (var i = 1; i <= 250; i++) {
      await _seedAsync(conn, deep, _msg(1, i), instance);
    }

    // The floor-cap fetch is what the fixed cap did on every trip.
    var atFloor = await coordinator.FetchInboxBatchAsync([deep], instance, maxPerStream: 100);
    await Assert.That(atFloor.Count).IsEqualTo(100)
      .Because("the previous behavior: a 250-row stream comes back 100 at a time, so clearing it "
             + "costs three sequential round-trips on a single drainer");

    // Same stream, the width the allocator grants once depth is known. The fetch returns rows
    // leased to this instance, so this is the same 250 rows seen through a wider window — which is
    // exactly the comparison that matters: one trip instead of three.
    var atCeiling = await coordinator.FetchInboxBatchAsync([deep], instance, maxPerStream: 1000);
    await Assert.That(atCeiling.Count).IsEqualTo(250)
      .Because("the wider cap really does widen the slice against a real store — the entire stream "
             + "in one trip rather than three. The fix is worth nothing if the cap did not carry "
             + "through to the SQL, and only a real database can show that it does");
    await Assert.That(atCeiling.Count).IsGreaterThan(atFloor.Count)
      .Because("this delta IS the throughput change: round-trips on a deep stream fall by the "
             + "ratio of ceiling to floor, and that stream is drained by one instance serially");
  }

  [Test]
  public async Task StreamsSharingOneCallAreBoundedIndividuallyNotCollectivelyAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var instance = Guid.Parse("0e0e0e0e-1111-4111-8111-111111111111");
    var a = Guid.Parse("0f0f0f0f-1111-4111-8111-111111111111");
    var b = Guid.Parse("1a1a1a1a-1111-4111-8111-111111111111");

    for (var i = 1; i <= 40; i++) { await _seedAsync(conn, a, _msg(2, i), instance); }
    for (var i = 1; i <= 40; i++) { await _seedAsync(conn, b, _msg(3, i), instance); }

    var rows = await coordinator.FetchInboxBatchAsync([a, b], instance, maxPerStream: 25);

    await Assert.That(rows.Count(r => r.StreamId == a)).IsEqualTo(25);
    await Assert.That(rows.Count(r => r.StreamId == b)).IsEqualTo(25)
      .Because("the cap is PER STREAM, so bucketing several streams into one call must not make "
             + "them share a single budget — if it did, adding a stream to a bucket would quietly "
             + "starve the others in it");
  }

  [Test]
  public async Task AShallowStreamReturnsOnlyWhatItHoldsAtAWideCapAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var instance = Guid.Parse("1b1b1b1b-1111-4111-8111-111111111111");
    var shallow = Guid.Parse("1c1c1c1c-1111-4111-8111-111111111111");

    for (var i = 1; i <= 3; i++) { await _seedAsync(conn, shallow, _msg(4, i), instance); }

    var rows = await coordinator.FetchInboxBatchAsync([shallow], instance, maxPerStream: 1000);

    await Assert.That(rows.Count).IsEqualTo(3)
      .Because("a wide cap costs nothing on a stream that has little — which is why quantizing a "
             + "tiny allocation up to the floor is safe, and why the budget is spent on rows "
             + "actually returned rather than on caps requested");
  }
}
