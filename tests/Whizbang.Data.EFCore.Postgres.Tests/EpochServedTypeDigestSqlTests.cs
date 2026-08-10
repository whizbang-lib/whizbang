using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Type-level digest answers are served FROM EPOCHS (#80-E): closed history composes by XOR of
/// immutable <c>wh_digest_epochs</c> rows and only the OPEN window (above the closure frontier)
/// is folded live — so a recompute-class manifest answer costs O(open window), not O(everything
/// ever stored). Before this, every <c>ComputeTypeDigestsAsync</c> call re-aggregated the whole
/// store; on a large store that unbounded fold is exactly what the epoch substrate exists to end.
///
/// <para>
/// The load-bearing semantic these tests pin: once sealed, an epoch row is AUTHORITATIVE for its
/// range. Manifest answers do not second-guess it against the store — detecting a bad seal is the
/// self-sweep's job (#80-D), not a per-answer recompute (which would re-buy the full-scan cost on
/// every answer). The sabotage test encodes that: a corrupted epoch row MUST flow into the answer.
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[Category("Integration")]
public class EpochServedTypeDigestSqlTests : EFCoreTestBase {

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

  /// <summary>Seeds one settled event; <paramref name="commitSeq"/> null = not yet stamped;
  /// <paramref name="origin"/> null = the local lane.</summary>
  private static async Task _seedAsync(NpgsqlConnection conn, Guid streamId, Guid eventId,
      string eventType, long? commitSeq, Guid? origin = null, string? tenant = null) {
    await using (var store = conn.CreateCommand()) {
      store.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
        VALUES (@event, @stream, @stream, 'TestAggregate', @type, @scope::jsonb, COALESCE(@seq, 9999),
                CASE WHEN @origin::uuid IS NULL THEN @seq ELSE nextval('wh_commit_seq') END,
                0, NOW() - INTERVAL '2 hours', @origin,
                CASE WHEN @origin::uuid IS NULL THEN NULL ELSE @seq END)
        """;
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue("stream", streamId);
      store.Parameters.AddWithValue("type", eventType);
      store.Parameters.AddWithValue("scope", tenant is null ? "null" : $"{{\"t\":\"{tenant}\"}}");
      store.Parameters.AddWithValue("seq", (object?)commitSeq ?? DBNull.Value);
      store.Parameters.AddWithValue("origin", (object?)origin ?? DBNull.Value);
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
    var rows = await cmd.ExecuteNonQueryAsync();
    if (rows == 0) {
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

  [Test]
  public async Task SealedEpoch_IsAuthoritative_ManifestAnswerReadsTheEpochNotTheStoreAsync() {
    // The discriminating test for the whole increment: corrupt a SEALED epoch's fold, then ask
    // for a type digest. Reading epochs → the corruption flows through (XOR'd with the live open
    // window). Re-aggregating the store → the corruption is invisible. The former is the design:
    // per-answer re-verification would re-buy the full-scan cost the epochs exist to end.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await _setWidthAsync(conn, 100);
    var stream = Guid.NewGuid();
    const string TYPE = "Contracts.EpochServedProbe";

    await _seedAsync(conn, stream, Guid.NewGuid(), TYPE, 5);
    await _seedAsync(conn, stream, Guid.NewGuid(), TYPE, 10);
    var open = Guid.NewGuid();
    await _seedAsync(conn, stream, open, TYPE, 150);   // epoch 1 — stays open (settled max inside it)
    await Assert.That(await _closeAsync(conn)).IsGreaterThanOrEqualTo(1);

    await _corruptEpochAsync(conn, TYPE, epochId: 0, lo: 12345, hi: 54321);
    var openFold = await _expectedFoldAsync(conn, open);

    var digests = await coordinator.ComputeTypeDigestsAsync(null, [TYPE], TimeSpan.FromHours(1));

    await Assert.That(digests.Count).IsEqualTo(1);
    await Assert.That(digests[0].DigestLo).IsEqualTo(12345 ^ openFold.Lo)
      .Because("the answer must be sealed-epoch XOR live-open-window — a live re-aggregation would hide the sabotage");
    await Assert.That(digests[0].DigestHi).IsEqualTo(54321 ^ openFold.Hi);
    await Assert.That(digests[0].EventCount).IsEqualTo(3)
      .Because("2 from the sealed epoch's stored count + 1 folded live from the open window");
  }

  [Test]
  public async Task EpochComposition_EqualsTheFullFold_AcrossClosedAndOpenRangesAsync() {
    // The stitching invariant: closed epochs and the open window partition the lane's sequence
    // space, so XOR-composing them must be bit-identical to folding everything — including a
    // settled row the stamper has not yet sequenced, which belongs to the open part by definition.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await _setWidthAsync(conn, 100);
    var stream = Guid.NewGuid();
    const string TYPE = "Contracts.EpochStitchProbe";

    var e1 = Guid.NewGuid();
    var e2 = Guid.NewGuid();
    var e3 = Guid.NewGuid();
    var unstamped = Guid.NewGuid();
    await _seedAsync(conn, stream, e1, TYPE, 5);
    await _seedAsync(conn, stream, e2, TYPE, 10);
    await _seedAsync(conn, stream, e3, TYPE, 150);
    await _seedAsync(conn, stream, unstamped, TYPE, null);   // settled but commit_sequence NULL
    _ = await _closeAsync(conn);

    var expected = await _expectedFoldAsync(conn, e1, e2, e3, unstamped);
    var digests = await coordinator.ComputeTypeDigestsAsync(null, [TYPE], TimeSpan.FromHours(1));

    await Assert.That(digests.Count).IsEqualTo(1);
    await Assert.That(digests[0].DigestLo).IsEqualTo(expected.Lo)
      .Because("composition over a partition must equal the whole — and the unstamped row must not fall through the crack");
    await Assert.That(digests[0].DigestHi).IsEqualTo(expected.Hi);
    await Assert.That(digests[0].EventCount).IsEqualTo(4);
  }

  [Test]
  public async Task LaneWithNoClosedEpochs_FallsBackToTheFullLiveFoldAsync() {
    // A fresh store (or a lane that never settled past an epoch boundary) has no frontier; the
    // answer must be the plain full fold, not empty — absence of epochs is not absence of events.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await _setWidthAsync(conn, 100);
    var stream = Guid.NewGuid();
    const string TYPE = "Contracts.EpochFallbackProbe";

    var e1 = Guid.NewGuid();
    var e2 = Guid.NewGuid();
    await _seedAsync(conn, stream, e1, TYPE, 3);
    await _seedAsync(conn, stream, e2, TYPE, 7);
    // Deliberately no close: settled max 7 → epoch 0 still open → no frontier advance for this data.

    var expected = await _expectedFoldAsync(conn, e1, e2);
    var digests = await coordinator.ComputeTypeDigestsAsync(null, [TYPE], TimeSpan.FromHours(1));

    await Assert.That(digests.Count).IsEqualTo(1);
    await Assert.That(digests[0].DigestLo).IsEqualTo(expected.Lo);
    await Assert.That(digests[0].EventCount).IsEqualTo(2);
  }

  [Test]
  public async Task ReceivedLane_ComposesFromItsOwnEpochs_KeyedOnOriginSequenceAsync() {
    // The consumer-side flavor: epochs for a received lane are keyed on the ORIGIN's sequence, so
    // the composition must be too — the local commit_sequence of a received row is unrelated.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await _setWidthAsync(conn, 100);
    var origin = Guid.NewGuid();
    var stream = Guid.NewGuid();
    const string TYPE = "Contracts.EpochReceivedProbe";

    await _seedAsync(conn, stream, Guid.NewGuid(), TYPE, 5, origin);
    await _seedAsync(conn, stream, Guid.NewGuid(), TYPE, 10, origin);
    var open = Guid.NewGuid();
    await _seedAsync(conn, stream, open, TYPE, 150, origin);
    await Assert.That(await _closeAsync(conn)).IsGreaterThanOrEqualTo(1);

    await _corruptEpochAsync(conn, TYPE, epochId: 0, lo: 777, hi: 888);
    var openFold = await _expectedFoldAsync(conn, open);

    var digests = await coordinator.ComputeTypeDigestsAsync(origin, [TYPE], TimeSpan.FromHours(1));

    await Assert.That(digests.Count).IsEqualTo(1);
    await Assert.That(digests[0].DigestLo).IsEqualTo(777 ^ openFold.Lo)
      .Because("the received lane's sealed epochs serve its answers, exactly like the local lane's serve local ones");
    await Assert.That(digests[0].EventCount).IsEqualTo(3);
  }

  [Test]
  public async Task TenantBuckets_SurviveTheComposition_AsSeparateRowsAsync() {
    // Epochs are bucketed per (tenant, type); the composed answer must keep those buckets
    // separate — collapsing tenants would make a divergence in one tenant smear across all.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await _setWidthAsync(conn, 100);
    var stream = Guid.NewGuid();
    const string TYPE = "Contracts.EpochTenantProbe";

    await _seedAsync(conn, stream, Guid.NewGuid(), TYPE, 5, tenant: "tenant-a");
    await _seedAsync(conn, Guid.NewGuid(), Guid.NewGuid(), TYPE, 10, tenant: "tenant-b");
    await _seedAsync(conn, stream, Guid.NewGuid(), TYPE, 150, tenant: "tenant-a");
    _ = await _closeAsync(conn);

    var digests = await coordinator.ComputeTypeDigestsAsync(null, [TYPE], TimeSpan.FromHours(1));

    await Assert.That(digests.Count).IsEqualTo(2);
    var tenants = digests.Select(d => d.TenantScope ?? "").OrderBy(t => t).ToList();
    await Assert.That(tenants).IsEquivalentTo(["tenant-a", "tenant-b"]);
    await Assert.That(digests.Single(d => d.TenantScope == "tenant-a").EventCount).IsEqualTo(2);
    await Assert.That(digests.Single(d => d.TenantScope == "tenant-b").EventCount).IsEqualTo(1);
  }
}
