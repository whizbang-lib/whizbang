using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Before <c>drop_all_overloads</c> was fixed to resolve its own schema, every signature change in
/// a multi-schema deployment silently left the OLD overload behind — the drop no-oped, and
/// <c>CREATE OR REPLACE</c> with different parameters creates a new overload beside the stale one.
/// Databases migrated through those versions carry duplicates that make unqualified calls
/// ambiguous (42725) and, on a return-type change, fail outright (42P13).
///
/// <para>The initializer now sweeps them: when a framework-defined function name has more than one
/// overload in the schema, the files defining that name are forced back into the run — the fixed
/// <c>drop_all_overloads</c> at the top of each clears every overload, and the file recreates the
/// single canonical one. Generic (no hardcoded signature list), one boot, and self-limiting: a
/// clean database never triggers it.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class DuplicateOverloadSweepTests : EFCoreTestBase {

  [Test]
  [Timeout(120000)]
  public async Task Initialize_WhenAStaleOverloadSurvivesFromAPreFixVersion_SweepsItAsync(
      CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    // Arrange — the legacy shape: a stale single-argument cleanup_stale_instances beside the
    // canonical two-argument one, exactly what the old current_schema()-based drop left behind
    // when v0.687 added p_definitive_dead_cutoff in a non-public schema.
    await _execAsync(conn, """
      CREATE OR REPLACE FUNCTION cleanup_stale_instances(p_stale_cutoff TIMESTAMPTZ)
      RETURNS TABLE(deleted_instance_id UUID) AS $$
      BEGIN RETURN; END;
      $$ LANGUAGE plpgsql;
      """, cancellationToken);
    await Assert.That(await _overloadCountAsync(conn, "cleanup_stale_instances", cancellationToken))
      .IsEqualTo(2).Because("precondition: the stale duplicate must actually be present");

    // Act — an ordinary startup. Nothing else changed, so without the sweep the fast path's
    // hash comparison sees a fully-migrated schema and skips everything.
    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    // Assert — one canonical overload remains, and an unqualified single-argument call (every
    // legacy caller's shape) resolves unambiguously to it.
    await Assert.That(await _overloadCountAsync(conn, "cleanup_stale_instances", cancellationToken))
      .IsEqualTo(1)
      .Because("a stale overload beside the canonical one makes unqualified calls ambiguous and "
             + "the next return-type change fail with 42P13 — the sweep exists to retire them");

    await using var call = conn.CreateCommand();
    call.CommandText = "SELECT COUNT(*) FROM cleanup_stale_instances(NOW() - INTERVAL '1 hour', NULL)";
    var callable = await call.ExecuteScalarAsync(cancellationToken);
    await Assert.That(callable is long).IsTrue()
      .Because("the surviving overload must be the canonical two-argument definition");
  }

  [Test]
  [Timeout(120000)]
  public async Task Initialize_OnACleanDatabase_DoesNotTriggerTheSweepAsync(
      CancellationToken cancellationToken) {
    // A clean schema (the test base fully initializes it) must stay on the fast path — the sweep
    // is a repair, not a per-boot tax. Observable: a second init on an untouched database leaves
    // every ledger row's status as the fast path left it (nothing re-applied).
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    await using var before = conn.CreateCommand();
    before.CommandText = "SELECT MAX(updated_at) FROM wh_schema_migrations WHERE owner = 'whizbang'";
    var updatedBefore = await before.ExecuteScalarAsync(cancellationToken);

    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    await using var after = conn.CreateCommand();
    after.CommandText = "SELECT MAX(updated_at) FROM wh_schema_migrations WHERE owner = 'whizbang'";
    var updatedAfter = await after.ExecuteScalarAsync(cancellationToken);

    await Assert.That(updatedAfter).IsEqualTo(updatedBefore)
      .Because("no duplicates → fast path → the ledger is untouched");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task<long> _overloadCountAsync(
      NpgsqlConnection conn, string name, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT COUNT(*) FROM pg_proc p JOIN pg_namespace n ON p.pronamespace = n.oid
      WHERE p.proname = @name AND n.nspname = 'public'
      """;
    cmd.Parameters.AddWithValue("name", name);
    return (long)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync(ct);
  }
}
