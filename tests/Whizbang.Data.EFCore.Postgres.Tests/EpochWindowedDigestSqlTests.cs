using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Negotiated-scope digest reads (#80-B): a manifest exchange where both sides agree on a
/// SEQUENCE WINDOW <c>[since, until)</c>, so verified history is never re-shipped. Half-open on
/// purpose: epoch boundaries align exactly (<c>[e*width, (e+1)*width)</c>) and the watermark
/// (<c>ComputedThrough</c>, the exclusive end actually covered) IS the next ask's <c>since</c>.
///
/// <para>
/// Window semantics pinned here:
/// an epoch FULLY inside the window contributes its sealed fold (authoritative — same rule as
/// the unwindowed path); a PARTIALLY covered epoch contributes a live fold of just the covered
/// fringe (a seal is indivisible — it cannot answer for half its range). The watermark is the
/// settled maximum capped by the requested <c>until</c>: an answer never covers what has not
/// settled, and the asker advances its seal only through what was actually answered.
/// </para>
///
/// <para>
/// Stream-level windowed reads page by stream id (<c>resumeAfter</c> + <c>maxDigests</c>) — the
/// second dimension of the resume cursor. A non-null returned cursor means the window is NOT
/// complete; the asker must not advance its seal past a partial window.
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[Category("Integration")]
public class EpochWindowedDigestSqlTests : EFCoreTestBase {

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task _setWidthAsync(NpgsqlConnection conn, long width) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('integrity_epoch_width', @w, 'integer', 'test epoch width')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """;
    cmd.Parameters.AddWithValue("w", width.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _seedAsync(NpgsqlConnection conn, Guid streamId, Guid eventId,
      string eventType, long commitSeq) {
    await using (var store = conn.CreateCommand()) {
      store.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, flags, created_at)
        VALUES (@event, @stream, @stream, 'TestAggregate', @type, 'null'::jsonb, @seq,
                @seq, 0, NOW() - INTERVAL '2 hours')
        """;
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue("stream", streamId);
      store.Parameters.AddWithValue("type", eventType);
      store.Parameters.AddWithValue("seq", commitSeq);
      await store.ExecuteNonQueryAsync();
    }
    await using (var body = conn.CreateCommand()) {
      body.CommandText = """
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@event, '{"seeded":true}'::jsonb, '{}'::jsonb)
        """;
      body.Parameters.AddWithValue("event", eventId);
      await body.ExecuteNonQueryAsync();
    }
  }

  private static async Task<int> _closeAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT close_digest_epochs(NOW(), 3600, 100)";
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _corruptEpochAsync(NpgsqlConnection conn, string eventType,
      long epochId, long lo, long hi) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      UPDATE wh_digest_epochs SET digest_lo = @lo, digest_hi = @hi
      WHERE event_type = @type AND epoch_id = @epoch
      """;
    cmd.Parameters.AddWithValue("lo", lo);
    cmd.Parameters.AddWithValue("hi", hi);
    cmd.Parameters.AddWithValue("type", eventType);
    cmd.Parameters.AddWithValue("epoch", epochId);
    if (await cmd.ExecuteNonQueryAsync() == 0) {
      throw new InvalidOperationException("sabotage found no epoch row — the test setup is wrong");
    }
  }

  private static async Task<(long Lo, long Hi)> _expectedFoldAsync(NpgsqlConnection conn, params Guid[] eventIds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT bit_xor(hashtextextended(x::text, 0)), bit_xor(hashtextextended(x::text, 1))
      FROM unnest(@ids::uuid[]) AS x
      """;
    cmd.Parameters.AddWithValue("ids", eventIds);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1));
  }

  /// <summary>Seeds the canonical fixture: epochs 0 and 1 sealed, epoch 2 open.
  /// Sequences 5, 10 | 150, 160 | 250. Returns the event ids by sequence.</summary>
  private static async Task<(Guid E5, Guid E10, Guid E150, Guid E160, Guid E250)> _seedCanonicalAsync(
      NpgsqlConnection conn, string type) {
    var stream = Guid.NewGuid();
    var e5 = Guid.NewGuid();
    var e10 = Guid.NewGuid();
    var e150 = Guid.NewGuid();
    var e160 = Guid.NewGuid();
    var e250 = Guid.NewGuid();
    await _seedAsync(conn, stream, e5, type, 5);
    await _seedAsync(conn, stream, e10, type, 10);
    await _seedAsync(conn, stream, e150, type, 150);
    await _seedAsync(conn, stream, e160, type, 160);
    await _seedAsync(conn, stream, e250, type, 250);
    await _closeAsync(conn);
    return (e5, e10, e150, e160, e250);
  }

  [Test]
  public async Task SettledMax_IsTheWatermarkCeiling_UnsettledNeverCountsAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    const string TYPE = "Contracts.WindowWatermarkProbe";
    var stream = Guid.NewGuid();

    await _seedAsync(conn, stream, Guid.NewGuid(), TYPE, 40);
    // A FRESH event at a higher sequence: settled-max must not include it — an answer claiming
    // coverage through an unsettled sequence would let the asker seal over an in-flight delivery.
    await using (var fresh = conn.CreateCommand()) {
      fresh.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, flags, created_at)
        VALUES (@e, @s, @s, 'TestAggregate', @t, 'null'::jsonb, 90, 90, 0, NOW())
        """;
      fresh.Parameters.AddWithValue("e", Guid.NewGuid());
      fresh.Parameters.AddWithValue("s", stream);
      fresh.Parameters.AddWithValue("t", TYPE);
      await fresh.ExecuteNonQueryAsync();
    }

    var settledMax = await coordinator.GetIntegritySettledMaxAsync(null, TimeSpan.FromHours(1));

    await Assert.That(settledMax).IsEqualTo(40)
      .Because("the watermark ceiling is the SETTLED max — sequence 90 is still in flight");
  }

  [Test]
  public async Task Window_FullyCoveringSealedEpochs_ComposesTheirSealsPlusTheLiveTailAsync() {
    // Window [0, ∞) spans sealed epochs 0 and 1 entirely plus the open tail. Sabotaged seals
    // MUST flow through — fully-covered epochs answer from their seal, never a recompute.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await _setWidthAsync(conn, 100);
    const string TYPE = "Contracts.WindowFullProbe";
    var (_, _, _, _, e250) = await _seedCanonicalAsync(conn, TYPE);

    await _corruptEpochAsync(conn, TYPE, 0, lo: 111, hi: 222);
    await _corruptEpochAsync(conn, TYPE, 1, lo: 333, hi: 444);
    var tail = await _expectedFoldAsync(conn, e250);

    var result = await coordinator.ComputeTypeDigestsWindowedAsync(
      null, [TYPE], sinceSequence: 0, untilSequence: null, TimeSpan.FromHours(1));

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.ComputedThrough).IsEqualTo(251)
      .Because("until was unbounded, so the answer covers everything settled; the watermark is the NEXT ask's since");
    await Assert.That(result.Digests.Count).IsEqualTo(1);
    await Assert.That(result.Digests[0].DigestLo).IsEqualTo(111 ^ 333 ^ tail.Lo)
      .Because("both fully-covered seals compose with the live open tail — a live re-aggregation would hide the sabotage");
    await Assert.That(result.Digests[0].EventCount).IsEqualTo(5);
  }

  [Test]
  public async Task Window_ExactlyOneSealedEpoch_AnswersFromTheSealAloneAsync() {
    // Window [100, 200) IS epoch 1. The answer is that seal, untouched by neighbors.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await _setWidthAsync(conn, 100);
    const string TYPE = "Contracts.WindowExactProbe";
    _ = await _seedCanonicalAsync(conn, TYPE);

    await _corruptEpochAsync(conn, TYPE, 1, lo: 999, hi: 888);

    var result = await coordinator.ComputeTypeDigestsWindowedAsync(
      null, [TYPE], sinceSequence: 100, untilSequence: 200, TimeSpan.FromHours(1));

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.ComputedThrough).IsEqualTo(200);
    await Assert.That(result.Digests.Count).IsEqualTo(1);
    await Assert.That(result.Digests[0].DigestLo).IsEqualTo(999)
      .Because("the window is exactly the sealed epoch — the seal IS the answer");
    await Assert.That(result.Digests[0].EventCount).IsEqualTo(2);
  }

  [Test]
  public async Task Window_PartiallyCoveringAnEpoch_FoldsTheFringeLive_SealNotConsultedAsync() {
    // Window [6, 151): epoch 0 is only partially covered (misses seq 5) and epoch 1 only
    // partially (misses 160-199). A seal is indivisible — it cannot answer for half its range —
    // so BOTH fringes fold live and the sabotage must NOT leak into the answer.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await _setWidthAsync(conn, 100);
    const string TYPE = "Contracts.WindowFringeProbe";
    var (_, e10, e150, _, _) = await _seedCanonicalAsync(conn, TYPE);

    await _corruptEpochAsync(conn, TYPE, 0, lo: 111, hi: 222);
    await _corruptEpochAsync(conn, TYPE, 1, lo: 333, hi: 444);
    var expected = await _expectedFoldAsync(conn, e10, e150);

    var result = await coordinator.ComputeTypeDigestsWindowedAsync(
      null, [TYPE], sinceSequence: 6, untilSequence: 151, TimeSpan.FromHours(1));

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Digests.Count).IsEqualTo(1);
    await Assert.That(result.Digests[0].DigestLo).IsEqualTo(expected.Lo)
      .Because("partially-covered epochs fold live over just the covered fringe — the seal answers only for its whole range");
    await Assert.That(result.Digests[0].EventCount).IsEqualTo(2);
  }

  [Test]
  public async Task Window_BeyondTheSettledMax_ClampsTheWatermarkAsync() {
    // Asking [0, 10_000) when only 250 has settled: the answer covers through 250 and SAYS so —
    // a watermark echoing the request would let the asker seal ranges nobody ever verified.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await _setWidthAsync(conn, 100);
    const string TYPE = "Contracts.WindowClampProbe";
    _ = await _seedCanonicalAsync(conn, TYPE);

    var result = await coordinator.ComputeTypeDigestsWindowedAsync(
      null, [TYPE], sinceSequence: 0, untilSequence: 10_000, TimeSpan.FromHours(1));

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.ComputedThrough).IsEqualTo(251)
      .Because("coverage is capped at what has settled, and the asker must learn the real cap");
  }

  [Test]
  public async Task StreamWindow_PagesByStreamId_WithAResumeCursorAsync() {
    // The second cursor dimension: within one sequence window, stream-level rows page by
    // stream id. A non-null cursor = the window is NOT complete — the asker keeps its seal put
    // and asks again from the cursor; the final page returns null.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await _setWidthAsync(conn, 100);
    const string TYPE = "Contracts.StreamPageProbe";

    var streams = Enumerable.Range(1, 3)
      .Select(n => Guid.Parse($"019f1111-0000-7000-8000-{n:D12}"))
      .ToList();
    for (var i = 0; i < streams.Count; i++) {
      await _seedAsync(conn, streams[i], Guid.NewGuid(), TYPE, 10 + i);
    }

    var page1 = await coordinator.ComputeStreamDigestsWindowedAsync(
      null, [TYPE], sinceSequence: 0, untilSequence: null, resumeAfterStreamId: null,
      maxDigests: 2, TimeSpan.FromHours(1));

    await Assert.That(page1).IsNotNull();
    await Assert.That(page1!.Digests.Count).IsEqualTo(2);
    await Assert.That(page1.Digests.Select(d => d.StreamId).ToList())
      .IsEquivalentTo([streams[0], streams[1]])
      .Because("pages walk stream ids in order — an arbitrary subset could never terminate");
    await Assert.That(page1.ResumeAfterStreamId).IsEqualTo(streams[1])
      .Because("a non-null cursor tells the asker the window is not complete — do not advance the seal");

    var page2 = await coordinator.ComputeStreamDigestsWindowedAsync(
      null, [TYPE], sinceSequence: 0, untilSequence: null, resumeAfterStreamId: page1.ResumeAfterStreamId,
      maxDigests: 2, TimeSpan.FromHours(1));

    await Assert.That(page2!.Digests.Count).IsEqualTo(1);
    await Assert.That(page2.Digests[0].StreamId).IsEqualTo(streams[2]);
    await Assert.That(page2.ResumeAfterStreamId).IsNull()
      .Because("the null cursor is the completion signal — only now may the asker seal the window");
    await Assert.That(page2.ComputedThrough).IsEqualTo(13)
      .Because("the settled max is 12, so coverage runs through it and the next window starts at 13");
  }

  [Test]
  public async Task StreamWindow_OnlyEventsInsideTheWindowContributeAsync() {
    // The sequence dimension of the cursor: rows outside [since, until) must not contribute to
    // any bucket — re-shipping verified history is exactly what negotiated scope exists to end.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await _setWidthAsync(conn, 100);
    const string TYPE = "Contracts.StreamWindowProbe";
    var stream = Guid.NewGuid();

    var below = Guid.NewGuid();
    var inside = Guid.NewGuid();
    var above = Guid.NewGuid();
    await _seedAsync(conn, stream, below, TYPE, 10);
    await _seedAsync(conn, stream, inside, TYPE, 20);
    await _seedAsync(conn, stream, above, TYPE, 30);

    var expected = await _expectedFoldAsync(conn, inside);
    var result = await coordinator.ComputeStreamDigestsWindowedAsync(
      null, [TYPE], sinceSequence: 15, untilSequence: 25, resumeAfterStreamId: null,
      maxDigests: 100, TimeSpan.FromHours(1));

    await Assert.That(result!.Digests.Count).IsEqualTo(1);
    await Assert.That(result.Digests[0].DigestLo).IsEqualTo(expected.Lo)
      .Because("seq 10 sits below since (already verified) and seq 30 at/above until — neither may re-ship");
    await Assert.That(result.Digests[0].EventCount).IsEqualTo(1);
  }
}
