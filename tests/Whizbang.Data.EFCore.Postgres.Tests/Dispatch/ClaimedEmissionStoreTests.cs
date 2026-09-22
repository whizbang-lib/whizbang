using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.EFCore.Postgres.Dispatch;

namespace Whizbang.Data.EFCore.Postgres.Tests.Dispatch;

/// <summary>
/// Locks the atomic-claim contract for <see cref="EFCoreClaimedEmissionStore"/>.
/// The store is the primitive backing <c>IDispatcher.PublishOnceAsync</c> —
/// these tests pin the invariant that two concurrent writers of the same
/// claim key collapse to exactly one winner, which is what the
/// SagaCompletedEvent emission-race fix depends on.
/// </summary>
/// <docs>fundamentals/dispatcher/publish-once</docs>
[Category("Integration")]
[Category("Dispatcher")]
[Category("Shard1")]
public class ClaimedEmissionStoreTests : EFCoreTestBase {

  // ── Schema invariant ──────────────────────────────────────────────────

  [Test]
  public async Task Migration060_WhUniqueEmissionClaims_ExistsInPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT EXISTS (
        SELECT 1 FROM pg_tables
        WHERE tablename = 'wh_unique_emission_claims'
          AND schemaname = 'public'
      );
      """;
    var exists = (bool)(await cmd.ExecuteScalarAsync())!;

    await Assert.That(exists).IsTrue()
      .Because("Migration 060 creates the table; without it no claim can ever be taken.");
  }

  // ── Race invariant: two concurrent claimants, exactly one wins ────────

  /// <summary>
  /// A consumer whose tables live in a service schema (HasDefaultSchema), with a
  /// connection whose search_path does not include that schema — the multi-schema
  /// deployment shape.
  /// </summary>
  private sealed class SchemaScopedDbContext(DbContextOptions<SchemaScopedDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.ConfigureWhizbangInfrastructure();
      modelBuilder.HasDefaultSchema(ServiceSchema);
    }
  }

  private const string ServiceSchema = "svc_claims";

  [Test]
  public async Task TryClaim_SchemaScopedDbContext_ClaimsInTheModelSchemaAsync() {
    // The migration creates the table schema-qualified (__SCHEMA__.wh_unique_emission_claims),
    // so in a multi-schema deployment the table exists ONLY in the service schema. A bare
    // INSERT resolves through search_path, which is not guaranteed to include that schema —
    // the same 42P01 shape move_to_dead_letters and the upsert strategies were already
    // fixed for. The store must qualify from the model, like EFCoreDeadLetterStore does.
    await using (var setup = CreateDbContext()) {
      var setupConn = (NpgsqlConnection)setup.Database.GetDbConnection();
      if (setupConn.State != System.Data.ConnectionState.Open) {
        await setupConn.OpenAsync();
      }

      await using var ddl = setupConn.CreateCommand();
      ddl.CommandText = $"""
        CREATE SCHEMA IF NOT EXISTS {ServiceSchema};
        CREATE TABLE IF NOT EXISTS {ServiceSchema}.wh_unique_emission_claims (
          claim_key TEXT PRIMARY KEY,
          claimed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
          claimed_by_event_id UUID NOT NULL,
          expires_at TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '30 minutes')
        );
        """;
      await ddl.ExecuteNonQueryAsync();
    }

    var options = new DbContextOptionsBuilder<SchemaScopedDbContext>()
      .UseNpgsql(ConnectionString)
      .Options;
    await using var schemaScoped = new SchemaScopedDbContext(options);
    var store = new EFCoreClaimedEmissionStore(schemaScoped);

    var key = $"schema-scoped:{Guid.NewGuid():N}";
    var claimed = await store.TryClaimAsync(key, TrackedGuid.NewMedo().Value, CancellationToken.None);

    await Assert.That(claimed).IsTrue()
      .Because("the model says where the table is; a store that trusts search_path instead "
             + "throws 42P01 against the very deployment shape the migration supports.");

    // And the row landed where the model said — not in public, which also has the table
    // in this test database (migration 060 ran there), so a bare INSERT that resolved to
    // public would return true while writing to the wrong schema.
    await using var verify = CreateDbContext();
    var verifyConn = (NpgsqlConnection)verify.Database.GetDbConnection();
    if (verifyConn.State != System.Data.ConnectionState.Open) {
      await verifyConn.OpenAsync();
    }

    await using var count = verifyConn.CreateCommand();
    count.CommandText = $"SELECT count(*) FROM {ServiceSchema}.wh_unique_emission_claims WHERE claim_key = @key;";
    var keyParam = count.CreateParameter();
    keyParam.ParameterName = "@key";
    keyParam.Value = key;
    count.Parameters.Add(keyParam);

    await Assert.That((long)(await count.ExecuteScalarAsync())!).IsEqualTo(1L)
      .Because("a claim that reports true but landed in another schema is exactly-once "
             + "against the wrong table, which is exactly-zero against the right one.");
  }

  [Test]
  public async Task TryClaim_TwoConcurrentSameKey_ExactlyOneWinsAsync() {
    // Two contexts = two NpgsqlConnections = real concurrency at PG. A
    // SELECT-then-INSERT impl would non-deterministically collide; the
    // ON CONFLICT DO NOTHING contract is the only impl that gives a
    // deterministic single-winner outcome with no thrown exception.
    await using var ctx1 = CreateDbContext();
    await using var ctx2 = CreateDbContext();
    var store1 = new EFCoreClaimedEmissionStore(ctx1);
    var store2 = new EFCoreClaimedEmissionStore(ctx2);

    var key = $"test:{Guid.NewGuid():N}";
    var evt1 = TrackedGuid.NewMedo();
    var evt2 = TrackedGuid.NewMedo();

    var results = await Task.WhenAll(
      store1.TryClaimAsync(key, evt1, CancellationToken.None),
      store2.TryClaimAsync(key, evt2, CancellationToken.None));

    var winners = results.Count(r => r);
    await Assert.That(winners).IsEqualTo(1)
      .Because("Atomic INSERT … ON CONFLICT yields exactly one INSERT for a given claim_key under any concurrency; one caller's affected-row count is 1, the other is 0.");

    var rowCount = await _countClaimRowsAsync(ctx1, key);
    await Assert.That(rowCount).IsEqualTo(1)
      .Because("Storage-side cross-check: only one row should ever land in the claim table for a contested key.");
  }

  // ── Sequential: second attempt on the same key returns false ─────────

  [Test]
  public async Task TryClaim_SecondAttemptSameKey_ReturnsFalseAsync() {
    await using var ctx = CreateDbContext();
    var store = new EFCoreClaimedEmissionStore(ctx);

    var key = $"test:{Guid.NewGuid():N}";
    var first = await store.TryClaimAsync(key, TrackedGuid.NewMedo(), CancellationToken.None);
    var second = await store.TryClaimAsync(key, TrackedGuid.NewMedo(), CancellationToken.None);

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse()
      .Because("Once claimed, the key stays claimed for the TTL window. A no-op return value (not a thrown conflict) is the contract.");
  }

  // ── Distinct keys are independent ────────────────────────────────────

  [Test]
  public async Task TryClaim_DistinctKeys_BothWinAsync() {
    await using var ctx = CreateDbContext();
    var store = new EFCoreClaimedEmissionStore(ctx);

    var keyA = $"test:{Guid.NewGuid():N}:A";
    var keyB = $"test:{Guid.NewGuid():N}:B";

    var a = await store.TryClaimAsync(keyA, TrackedGuid.NewMedo(), CancellationToken.None);
    var b = await store.TryClaimAsync(keyB, TrackedGuid.NewMedo(), CancellationToken.None);

    await Assert.That(a).IsTrue();
    await Assert.That(b).IsTrue()
      .Because("Different keys are independent; the table partitions claims by key, not globally.");
  }

  // ── Audit: claimed_by_event_id is the winner's event id ──────────────

  [Test]
  public async Task TryClaim_RecordsClaimedByEventIdOfWinnerAsync() {
    await using var ctx = CreateDbContext();
    var store = new EFCoreClaimedEmissionStore(ctx);

    var key = $"test:{Guid.NewGuid():N}";
    var winnerEventId = TrackedGuid.NewMedo();

    var taken = await store.TryClaimAsync(key, winnerEventId, CancellationToken.None);
    await Assert.That(taken).IsTrue();

    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT claimed_by_event_id FROM wh_unique_emission_claims WHERE claim_key = @key";
    var p = cmd.CreateParameter();
    p.ParameterName = "@key";
    p.Value = key;
    cmd.Parameters.Add(p);

    var stored = (Guid)(await cmd.ExecuteScalarAsync())!;
    await Assert.That(stored).IsEqualTo(winnerEventId)
      .Because("The audit column records the winner's MessageId so on-call can identify which event won a contested race.");
  }

  // ── Default expires_at lands inside the documented 30-minute TTL ─────

  [Test]
  public async Task TryClaim_DefaultExpiresAtIs30MinutesFromClaimedAtAsync() {
    await using var ctx = CreateDbContext();
    var store = new EFCoreClaimedEmissionStore(ctx);

    var key = $"test:{Guid.NewGuid():N}";
    var taken = await store.TryClaimAsync(key, TrackedGuid.NewMedo(), CancellationToken.None);
    await Assert.That(taken).IsTrue();

    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT EXTRACT(EPOCH FROM (expires_at - claimed_at))::int
      FROM wh_unique_emission_claims
      WHERE claim_key = @key
      """;
    var p = cmd.CreateParameter();
    p.ParameterName = "@key";
    p.Value = key;
    cmd.Parameters.Add(p);

    var ttlSeconds = (int)(await cmd.ExecuteScalarAsync())!;
    await Assert.That(ttlSeconds).IsEqualTo(1800)
      .Because("Default TTL is 30 minutes = 1800 seconds. Changing this is a deliberate decision tied to the prune sweep; if the prune cadence ever shortens to where 30 min is too long, this test must change together with it.");
  }

  // ── Helpers ──────────────────────────────────────────────────────────

  private static async Task<int> _countClaimRowsAsync(DbContext ctx, string key) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_unique_emission_claims WHERE claim_key = @key";
    var p = cmd.CreateParameter();
    p.ParameterName = "@key";
    p.Value = key;
    cmd.Parameters.Add(p);
    return (int)(long)(await cmd.ExecuteScalarAsync())!;
  }
}
