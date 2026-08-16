using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// <c>drop_all_overloads</c> used to filter by <c>current_schema()</c> — but pooled EF connections
/// strip <c>search_path</c> (migrations README rule 10), so <c>current_schema()</c> answers
/// <c>public</c> regardless of the schema actually being migrated. In every multi-schema
/// deployment the drop silently no-oped, and the first signature-changing migration to rely on it
/// (106, <c>record_heartbeat VOID → BOOLEAN</c>) failed with 42P13 because the overload it had
/// "dropped" was still there. The helper now takes its schema from the <c>__SCHEMA__</c>
/// substitution baked into its own body.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/000_MigrationTracking.sql</code-under-test>
[Category("Migrations")]
[NotInParallel("EFCorePostgresTests")]
public class DropAllOverloadsSchemaResolutionTests : EFCoreTestBase {

  [Test]
  [Timeout(60000)]
  public async Task DropAllOverloads_InANonPublicSchema_WithStrippedSearchPath_DropsTheFunctionAsync(
      CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    // A second schema with its own helper — the multi-schema shape, exactly as the EFCore
    // generator substitutes it (quoted identifier in the body's string literal).
    await _execAsync(conn, """
      CREATE SCHEMA IF NOT EXISTS fence_test;
      CREATE OR REPLACE FUNCTION fence_test.drop_all_overloads(p_function_name TEXT)
      RETURNS VOID AS $$
      DECLARE
        _oid oid;
      BEGIN
        FOR _oid IN
          SELECT p.oid FROM pg_proc p
          JOIN pg_namespace n ON p.pronamespace = n.oid
          WHERE p.proname = p_function_name
            AND n.nspname = replace('"fence_test"', '"', '')
        LOOP
          EXECUTE format('DROP FUNCTION IF EXISTS %s CASCADE', _oid::regprocedure);
        END LOOP;
      END;
      $$ LANGUAGE plpgsql;
      CREATE OR REPLACE FUNCTION fence_test.victim() RETURNS VOID AS $$ BEGIN END $$ LANGUAGE plpgsql;
      """, cancellationToken);

    // The CI failure's shape: the migrating connection's search_path does NOT contain the schema
    // being migrated — current_schema() answers 'public'.
    await _execAsync(conn, "SET search_path TO public", cancellationToken);
    await _execAsync(conn, "SELECT fence_test.drop_all_overloads('victim')", cancellationToken);

    await using var verify = conn.CreateCommand();
    verify.CommandText = """
      SELECT EXISTS (
        SELECT 1 FROM pg_proc p JOIN pg_namespace n ON p.pronamespace = n.oid
        WHERE p.proname = 'victim' AND n.nspname = 'fence_test')
      """;
    var survived = (bool)(await verify.ExecuteScalarAsync(cancellationToken))!;

    await Assert.That(survived).IsFalse()
      .Because("the helper must drop overloads in ITS OWN schema, not wherever search_path points — "
             + "a current_schema()-based lookup silently no-ops here and the next signature change "
             + "fails with 42P13");
  }

  // The end-to-end consequence: a VOID→BOOLEAN signature change in a non-public schema must apply.
  [Test]
  [Timeout(60000)]
  public async Task SignatureChange_InANonPublicSchema_AppliesWithoutError42P13Async(
      CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    await _execAsync(conn, """
      CREATE SCHEMA IF NOT EXISTS fence_test2;
      CREATE OR REPLACE FUNCTION fence_test2.drop_all_overloads(p_function_name TEXT)
      RETURNS VOID AS $$
      DECLARE
        _oid oid;
      BEGIN
        FOR _oid IN
          SELECT p.oid FROM pg_proc p
          JOIN pg_namespace n ON p.pronamespace = n.oid
          WHERE p.proname = p_function_name
            AND n.nspname = replace('"fence_test2"', '"', '')
        LOOP
          EXECUTE format('DROP FUNCTION IF EXISTS %s CASCADE', _oid::regprocedure);
        END LOOP;
      END;
      $$ LANGUAGE plpgsql;
      CREATE OR REPLACE FUNCTION fence_test2.rh() RETURNS VOID AS $$ BEGIN END $$ LANGUAGE plpgsql;
      """, cancellationToken);

    await _execAsync(conn, "SET search_path TO public", cancellationToken);

    // Migration-106 shape: drop, then recreate with a different return type.
    await _execAsync(conn, """
      SELECT fence_test2.drop_all_overloads('rh');
      CREATE OR REPLACE FUNCTION fence_test2.rh() RETURNS BOOLEAN AS $$ BEGIN RETURN TRUE; END $$ LANGUAGE plpgsql;
      """, cancellationToken);

    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT fence_test2.rh()";
    var result = await verify.ExecuteScalarAsync(cancellationToken);

    await Assert.That(result is bool b && b).IsTrue()
      .Because("the boolean redefinition must have replaced the void one — 42P13 is what the old "
             + "current_schema() lookup produced in every multi-schema deployment");
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync(ct);
  }
}
