using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Configuration;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage-round-23 pass over five otherwise-isolated <see cref="EFCoreWorkCoordinator{TDbContext}"/>
/// branches: the negotiated-scope digest reads' "nothing settled yet" answer, the integrity
/// checkpoint watermark's never-regress clamp, and three schema-shape-drift fallbacks (an older
/// <c>get_stream_events</c>/<c>fetch_outbox_batch</c>/<c>fetch_inbox_batch</c> without a column a
/// newer C# consumer expects). Each of these degrades a specific way on purpose; the tests lock
/// the direction of the degradation, not just that a line ran.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class EFCoreWorkCoordinatorCoverageTests : EFCoreTestBase {

  private static async Task<NpgsqlConnection> _openConnectionAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  // ── Negotiated-scope windowed digests: nothing has settled yet ─────────────

  [Test]
  public async Task ComputeTypeDigestsWindowedAsync_NothingSettledYet_ReturnsEmptyResultAnchoredAtSinceAsync() {
    // If this ever returned null instead of an empty-but-anchored result, or advanced
    // ComputedThrough past what actually settled, the asker would either crash on a null answer
    // or seal a range nobody verified -- silently trusting delivery of events that have not
    // actually settled yet.
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    var result = await coordinator.ComputeTypeDigestsWindowedAsync(
      null, ["Contracts.NothingEverArrived"], sinceSequence: 5L, untilSequence: null, TimeSpan.FromHours(1));

    await Assert.That(result).IsNotNull()
      .Because("an empty-but-honest answer must still be a real WindowedDigestResult, not null -- " +
               "the asker needs a watermark to keep polling from, not an exception to handle");
    await Assert.That(result!.Digests).IsEmpty();
    await Assert.That(result.ComputedThrough).IsEqualTo(5L)
      .Because("the watermark must stay exactly at the asker's own since -- claiming progress " +
               "with nothing settled is how a seal drifts past what was actually verified");
  }

  [Test]
  public async Task ComputeStreamDigestsWindowedAsync_NothingSettledYet_ReturnsEmptyResultAnchoredAtSinceAsync() {
    // Sibling of the type-level check, for the stream-level paged read: an empty lane must still
    // answer with an anchored, non-null result and no resume cursor -- a non-null cursor here
    // would tell the asker the window is incomplete and to keep paging forever.
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    var result = await coordinator.ComputeStreamDigestsWindowedAsync(
      null, ["Contracts.NothingEverArrived"], sinceSequence: 7L, untilSequence: null,
      resumeAfterStreamId: null, maxDigests: 10, TimeSpan.FromHours(1));

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Digests).IsEmpty();
    await Assert.That(result.ComputedThrough).IsEqualTo(7L)
      .Because("nothing settled means the watermark cannot move past the asker's own since");
    await Assert.That(result.ResumeAfterStreamId).IsNull()
      .Because("a null cursor is the completion signal -- a non-null one here would tell the " +
               "asker to keep paging a window that was never opened");
  }

  // ── Integrity checkpoint watermark: never regress ──────────────────────────

  [Test]
  public async Task AdvanceIntegrityCheckpointAsync_StoredWatermarkAheadOfStampedHead_ClampsRatherThanRegressesAsync() {
    // If a stored watermark ever reads higher than the store's current stamped head (a manually
    // restored settings row, a replica momentarily behind what it last advanced to), the next
    // advance must clamp to the stored value rather than compute a window that runs backward.
    // Letting it regress would let the following CAS persist a SMALLER watermark, re-opening a
    // range that was already reported as checkpointed for re-detection as a false gap.
    await using var ctx = CreateDbContext();
    var conn = await _openConnectionAsync(ctx);
    const long inflatedWatermark = 999_999L;

    await using (var seed = conn.CreateCommand()) {
      seed.CommandText = """
        INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
        VALUES ('integrity_checkpoint_watermark', @val, 'integer', 'coverage-test seeded watermark')
        ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
        """;
      seed.Parameters.AddWithValue("val", inflatedWatermark.ToString(System.Globalization.CultureInfo.InvariantCulture));
      await seed.ExecuteNonQueryAsync();
    }

    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    var window = await coordinator.AdvanceIntegrityCheckpointAsync();

    await Assert.That(window).IsNotNull();
    await Assert.That(window!.FromCommitSequence).IsEqualTo(inflatedWatermark)
      .Because("the window floor must stay at the stored watermark, never fall back to the " +
               "store's lower real head");
    await Assert.That(window.ToCommitSequence).IsEqualTo(inflatedWatermark)
      .Because("the clamp pins the new head at the prior watermark rather than the lower real " +
               "max -- this is exactly what keeps the following CAS from persisting a smaller value");
    await Assert.That(window.Buckets).IsEmpty()
      .Because("a clamped (unchanged) window has nothing new to count");
  }

  // ── Schema-shape drift: older SQL function revisions ───────────────────────

  [Test]
  public async Task GetStreamEventsAsync_OlderSqlFunctionWithoutNewerColumns_FallsBackToLegacyDefaultsAsync() {
    // A get_stream_events revision from before out_perspective_name / out_commit_sequence /
    // out_attempts landed must not throw the perspective drainer -- it must degrade every added
    // field to its documented legacy default so a mid-rollout mix of package versions against an
    // unmigrated database keeps draining instead of taking the worker down.
    await using var ctx = CreateDbContext();
    var conn = await _openConnectionAsync(ctx);

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var workId = Guid.NewGuid();

    await using (var replace = conn.CreateCommand()) {
      replace.CommandText = """
        DROP FUNCTION IF EXISTS get_stream_events(uuid, uuid[], timestamptz, integer);
        CREATE FUNCTION get_stream_events(
          p_instance_id UUID,
          p_stream_ids UUID[],
          p_now TIMESTAMPTZ DEFAULT NOW(),
          p_lease_seconds INTEGER DEFAULT 300
        ) RETURNS TABLE(
          out_stream_id UUID,
          out_event_id UUID,
          out_event_type TEXT,
          out_event_data TEXT,
          out_metadata TEXT,
          out_scope TEXT,
          out_event_work_id UUID
        ) AS $fn$
          SELECT __STREAM_ID__::uuid, __EVENT_ID__::uuid, 'Contracts.Legacy'::text,
                 '{}'::text, NULL::text, NULL::text, __WORK_ID__::uuid
        $fn$ LANGUAGE sql;
        """
        .Replace("__STREAM_ID__", $"'{streamId}'")
        .Replace("__EVENT_ID__", $"'{eventId}'")
        .Replace("__WORK_ID__", $"'{workId}'");
      await replace.ExecuteNonQueryAsync();
    }

    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    var rows = await coordinator.GetStreamEventsAsync(instanceId, [streamId]);

    await Assert.That(rows.Count).IsEqualTo(1)
      .Because("the legacy-shaped function must still hand back the row it found");
    await Assert.That(rows[0].PerspectiveName).IsNull()
      .Because("no out_perspective_name column on this revision -- the cooldown gate must fall " +
               "back to legacy all-rows-under-eventid semantics rather than throw");
    await Assert.That(rows[0].CommitSequence).IsNull()
      .Because("no out_commit_sequence column -- consumers must fall back to event_id ordering");
    await Assert.That(rows[0].Attempts).IsEqualTo(0)
      .Because("no out_attempts column -- the DLQ attempts check must default to zero (a no-op), " +
               "not an exception that stalls the whole drain");
  }

  [Test]
  public async Task FetchOutboxBatchAsync_OlderSqlFunctionWithoutNewerColumns_FallsBackToLegacyDefaultsAsync() {
    // Slice 26.6b (commit_sequence/origin_*) and the Slice 1 error column both arrived after this
    // function's original shape. A drain against a database still on the older fetch_outbox_batch
    // must keep publishing -- degrading the newer envelope-stamping fields to null -- rather than
    // an ordinal lookup throwing and stalling the whole outbox drain for every stream in the batch.
    await using var ctx = CreateDbContext();
    var conn = await _openConnectionAsync(ctx);

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    await using (var replace = conn.CreateCommand()) {
      replace.CommandText = """
        DROP FUNCTION IF EXISTS fetch_outbox_batch(uuid[], uuid, integer, bigint);
        CREATE FUNCTION fetch_outbox_batch(
          p_stream_ids UUID[],
          p_instance_id UUID,
          p_max_per_stream INTEGER DEFAULT 100,
          p_max_bytes BIGINT DEFAULT NULL
        ) RETURNS TABLE(
          message_id UUID,
          stream_id UUID,
          destination VARCHAR(200),
          message_type VARCHAR(500),
          envelope_type VARCHAR(500),
          event_data TEXT,
          metadata JSONB,
          scope JSONB,
          status INTEGER,
          attempts INTEGER,
          partition_number INTEGER,
          is_event BOOLEAN
        ) AS $fn$
          SELECT __MESSAGE_ID__::uuid, __STREAM_ID__::uuid, 'legacy-dest'::varchar,
                 'Legacy.Message'::varchar, NULL::varchar, '{}'::text, '{}'::jsonb, NULL::jsonb,
                 1, 0, 0, false
        $fn$ LANGUAGE sql;
        """
        .Replace("__MESSAGE_ID__", $"'{messageId}'")
        .Replace("__STREAM_ID__", $"'{streamId}'");
      await replace.ExecuteNonQueryAsync();
    }

    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    var rows = await coordinator.FetchOutboxBatchAsync([streamId], instanceId);

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].MessageType).IsEqualTo("Legacy.Message")
      .Because("basic row hydration must still succeed against the older column set");
    await Assert.That(rows[0].CommitSequence).IsNull()
      .Because("no commit_sequence/origin_* columns on this revision -- the publisher must fall " +
               "back to publishing without the envelope stamp, never throw and drop the batch");
    await Assert.That(rows[0].OriginServiceId).IsNull();
    await Assert.That(rows[0].OriginCommitSequence).IsNull();
    await Assert.That(rows[0].Error).IsNull()
      .Because("no error column on this revision -- the pre-publish DLQ gate must fall back to " +
               "the meta-message rather than fail the lookup");
  }

  [Test]
  public async Task FetchInboxBatchAsync_OlderSqlFunctionWithoutErrorColumn_FallsBackToLegacyDefaultAsync() {
    // The v0.651 inbox forensic-preservation slice added the error column after this function's
    // original shape. A drain against a database still on the older fetch_inbox_batch must keep
    // dispatching -- leaving InboxWork.Error null -- rather than an ordinal lookup throwing and
    // stalling every stream's inbox drain.
    await using var ctx = CreateDbContext();
    var conn = await _openConnectionAsync(ctx);

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    await using (var replace = conn.CreateCommand()) {
      replace.CommandText = """
        DROP FUNCTION IF EXISTS fetch_inbox_batch(uuid[], uuid, integer, bigint);
        CREATE FUNCTION fetch_inbox_batch(
          p_stream_ids UUID[],
          p_instance_id UUID,
          p_max_per_stream INTEGER DEFAULT 100,
          p_max_bytes BIGINT DEFAULT NULL
        ) RETURNS TABLE(
          message_id UUID,
          stream_id UUID,
          handler_name VARCHAR(200),
          message_type VARCHAR(500),
          event_data TEXT,
          metadata JSONB,
          scope JSONB,
          status INTEGER,
          attempts INTEGER,
          partition_number INTEGER,
          is_event BOOLEAN
        ) AS $fn$
          SELECT __MESSAGE_ID__::uuid, __STREAM_ID__::uuid, 'LegacyHandler'::varchar,
                 'Legacy.Message'::varchar, '{}'::text, '{}'::jsonb, NULL::jsonb, 1, 0, 0, false
        $fn$ LANGUAGE sql;
        """
        .Replace("__MESSAGE_ID__", $"'{messageId}'")
        .Replace("__STREAM_ID__", $"'{streamId}'");
      await replace.ExecuteNonQueryAsync();
    }

    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    var rows = await coordinator.FetchInboxBatchAsync([streamId], instanceId);

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].MessageType).IsEqualTo("Legacy.Message")
      .Because("basic row hydration must still succeed against the older column set");
    await Assert.That(rows[0].Error).IsNull()
      .Because("no error column on this revision -- InboxWork.Error must default to null instead " +
               "of the lookup throwing and stalling the inbox drain");
  }

  // ── Local service identity: a value that cannot be read as a Guid ─────────

  private sealed class NonGuidServiceIdDbContext(DbContextOptions<NonGuidServiceIdDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.ConfigureWhizbangInfrastructure();
      modelBuilder.HasDefaultSchema("cov_svc_identity");
    }
  }

  [Test]
  public async Task GetLocalServiceIdAsync_ServiceIdColumnIsNotAGuid_ReturnsEmptyRatherThanThrowingAsync() {
    // A service_id value the reader cannot recognize as a Guid (a corrupted row, or a future
    // migration widening the column ahead of the reader) must degrade to Guid.Empty -- the same
    // "unknown identity" signal a missing row produces -- rather than surfacing an exception from
    // the local-identity lookup every outgoing envelope's SourceServiceId depends on.
    await using (var setup = CreateDbContext()) {
      var conn = await _openConnectionAsync(setup);
      await using var ddl = conn.CreateCommand();
      ddl.CommandText = """
        CREATE SCHEMA IF NOT EXISTS cov_svc_identity;
        CREATE TABLE IF NOT EXISTS cov_svc_identity.wh_service_config (
          single_row BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (single_row),
          service_id TEXT NOT NULL DEFAULT 'not-a-guid',
          service_name TEXT NOT NULL DEFAULT 'unknown',
          created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        INSERT INTO cov_svc_identity.wh_service_config (single_row, service_id)
        VALUES (TRUE, 'not-a-guid')
        ON CONFLICT (single_row) DO UPDATE SET service_id = EXCLUDED.service_id;
        """;
      await ddl.ExecuteNonQueryAsync();
    }

    var options = new DbContextOptionsBuilder<NonGuidServiceIdDbContext>()
      .UseNpgsql(ConnectionString)
      .Options;
    await using var schemaScoped = new NonGuidServiceIdDbContext(options);
    var coordinator = new EFCoreWorkCoordinator<NonGuidServiceIdDbContext>(
      schemaScoped, JsonContextRegistry.CreateCombinedOptions());

    var actual = await coordinator.GetLocalServiceIdAsync(CancellationToken.None);

    await Assert.That(actual).IsEqualTo(Guid.Empty)
      .Because("a scalar the reader cannot recognize as a Guid must read as 'no identity', not " +
               "crash the lookup every publish depends on");
  }
}

/// <summary>
/// <see cref="OrphanedEventRow"/> is the <c>SqlQueryRaw</c> materialization target for
/// <c>GetOrphanedLifecycleEventsAsync</c>. Its <c>Metadata</c> column is carried on the row but
/// (as of this writing) never read back out by <c>_deserializeEventEnvelope</c> -- this is a
/// straightforward property-contract lock, not a deep behavioral test: if the accessor pair ever
/// stopped round-tripping, forensic metadata for orphaned events would be silently dropped with no
/// other test in the file positioned to notice, since the deserialization path never reads it.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard1")]
public class OrphanedEventRowCoverageTests {

  [Test]
  public async Task OrphanedEventRow_Metadata_RoundTripsTheAssignedValueAsync() {
    var row = new OrphanedEventRow { Metadata = "{\"scheduleId\":\"s-1\"}" };

    await Assert.That(row.Metadata).IsEqualTo("{\"scheduleId\":\"s-1\"}")
      .Because("the row must carry back exactly what was assigned to it -- a silently dropped or " +
               "defaulted value here corrupts forensic data with no visible symptom elsewhere");
  }
}
