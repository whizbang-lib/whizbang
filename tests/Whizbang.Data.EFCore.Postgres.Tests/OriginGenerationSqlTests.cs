using System;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #80-F: origin generation — seal coherence across LEGITIMATE history mutation. A close
/// ("close the books" truncation) or a reclassification changes what a fold over already-sealed
/// history computes; without a signal, every consumer's seal would disagree with the origin
/// forever and the next sweep would alarm on what was actually deliberate. The two mutation
/// sites now (a) refold the affected sealed epochs inline — the origin's own answers are correct
/// immediately, not next sweep — and (b) bump the origin generation, which rides every manifest
/// so consumers know to reset their seals and re-verify instead of alarming.
///
/// <para>
/// The consumer-side guard is one atomic call: same generation = proceed; a CHANGED generation
/// resets the seal to zero, records the new generation, and tells the caller to skip this
/// comparison (its windows were aligned to the old world).
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[Category("Integration")]
public class OriginGenerationSqlTests : EFCoreTestBase {

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
      string eventType, long commitSeq, long version) {
    await using (var store = conn.CreateCommand()) {
      store.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, flags, created_at)
        VALUES (@event, @stream, @stream, 'TestAggregate', @type, 'null'::jsonb, @version,
                @seq, 0, NOW() - INTERVAL '2 hours')
        """;
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue("stream", streamId);
      store.Parameters.AddWithValue("type", eventType);
      store.Parameters.AddWithValue("version", version);
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

  private static async Task<long> _generationAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COALESCE((SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_origin_generation'), 0)";
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<int> _closeEpochsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT close_digest_epochs(NOW(), 3600, 100)";
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<long?> _epochCountForAsync(NpgsqlConnection conn, string type, long epochId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_count::bigint FROM wh_digest_epochs WHERE event_type = @t AND epoch_id = @e";
    cmd.Parameters.AddWithValue("t", type);
    cmd.Parameters.AddWithValue("e", epochId);
    var result = await cmd.ExecuteScalarAsync();
    return result is long c ? c : null;
  }

  [Test]
  public async Task CloseStream_BumpsTheGeneration_AndRefoldsTheAffectedSealedEpochAsync() {
    // A close is a LEGITIMATE truncation of sealed history. The sealed epoch must reflect the
    // post-close world immediately — an origin serving stale seals until the next nightly sweep
    // would report divergence against its own recompute all day — and the generation must move,
    // or every consumer's seal would silently disagree forever.
    await using var conn = await _openAsync();
    await _setWidthAsync(conn, 100);
    var stream = Guid.NewGuid();
    const string TYPE = "Contracts.GenerationCloseProbe";

    await _seedAsync(conn, stream, Guid.NewGuid(), TYPE, 5, version: 5);
    await _seedAsync(conn, stream, Guid.NewGuid(), TYPE, 10, version: 10);
    await _seedAsync(conn, stream, Guid.NewGuid(), TYPE, 150, version: 11);   // the carry-forward
    _ = await _closeEpochsAsync(conn);
    await Assert.That(await _epochCountForAsync(conn, TYPE, 0)).IsEqualTo(2)
      .Because("precondition: epoch 0 is sealed over the two events the close will truncate");
    var generationBefore = await _generationAsync(conn);

    await using (var close = conn.CreateCommand()) {
      close.CommandText = "SELECT close_status FROM close_stream(@s, 10, false)";
      close.Parameters.AddWithValue("s", stream);
      await Assert.That((string)(await close.ExecuteScalarAsync())!).IsEqualTo("closed");
    }

    await Assert.That(await _generationAsync(conn)).IsGreaterThan(generationBefore)
      .Because("truncating folded history without moving the generation would turn every consumer's next sweep into a false alarm");
    await Assert.That(await _epochCountForAsync(conn, TYPE, 0)).IsNull()
      .Because("the sealed epoch refolds INLINE to the post-close world (both folded events truncated → empty bucket)");
  }

  [Test]
  public async Task Reclassify_BumpsTheGeneration_AndRefoldsTheAffectedSealedEpochAsync() {
    // Reclassification removes a type's rows from the fold (flags & 8 excluded) — the same
    // legitimate-mutation shape as a close, with the same two obligations.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await _setWidthAsync(conn, 100);
    var stream = Guid.NewGuid();
    const string TYPE = "Contracts.GenerationReclassifyProbe";

    await _seedAsync(conn, stream, Guid.NewGuid(), TYPE, 5, version: 5);
    await _seedAsync(conn, stream, Guid.NewGuid(), TYPE, 150, version: 6);
    _ = await _closeEpochsAsync(conn);
    await Assert.That(await _epochCountForAsync(conn, TYPE, 0)).IsEqualTo(1);
    var generationBefore = await _generationAsync(conn);

    var result = await coordinator.ReclassifyEventsEphemeralAsync([TYPE]);
    await Assert.That(result.EventsReclassified).IsEqualTo(2);

    await Assert.That(await _generationAsync(conn)).IsGreaterThan(generationBefore);
    await Assert.That(await _epochCountForAsync(conn, TYPE, 0)).IsNull()
      .Because("reclassified rows leave the fold — the sealed epoch refolds to an empty bucket inline");
  }

  [Test]
  public async Task SealGenerationGuard_ResetsTheSealOnce_ThenProceedsAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();

    await Assert.That(await coordinator.EnsureIntegritySealGenerationAsync(origin, 5)).IsTrue()
      .Because("first contact has no seal to invalidate — record the generation and proceed");
    await coordinator.AdvanceIntegritySealAsync(origin, 400);
    await Assert.That(await coordinator.EnsureIntegritySealGenerationAsync(origin, 5)).IsTrue()
      .Because("an unchanged generation is the steady state — the seal stands");
    await Assert.That(await coordinator.GetIntegritySealAsync(origin)).IsEqualTo(400L);

    await Assert.That(await coordinator.EnsureIntegritySealGenerationAsync(origin, 6)).IsFalse()
      .Because("a changed generation means the origin's history legitimately moved — this round's windows are aligned to the old world");
    await Assert.That(await coordinator.GetIntegritySealAsync(origin)).IsEqualTo(0L)
      .Because("the seal resets so the next audit re-verifies from the beginning (cheap — the origin answers from epochs)");

    await Assert.That(await coordinator.EnsureIntegritySealGenerationAsync(origin, 6)).IsTrue()
      .Because("the reset happens ONCE per generation change, not on every manifest");
  }
}
