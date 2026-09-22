using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// A database can hold a function that is generations behind its last-word migration while every ledger
/// row says "hash unchanged": a replay that predates the redefinition closure re-ran an earlier definer
/// after the last word, and nothing since has had a reason to re-run the last word. The observed shape was
/// the ledger-aware <c>reconcile_message_type_registry</c> (migration 064) replaced by its pre-ledger
/// predecessor (040), so every recorded former name read as unacknowledged drift on every boot.
///
/// <para>The initializer now compares each framework function's deployed body with its last-word migration
/// body and re-runs the last-word file when they differ or the function is missing. Generic (no hardcoded
/// function list), one boot, and self-limiting: a database that matches its files never triggers it.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationFunctionBodies.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class StaleFunctionDefinitionSweepTests : EFCoreTestBase {

  private const string PINNED_ID = "6f0d6a0e-3c2f-4b1a-9d7e-8c5b4a3f2e10";
  private const string FORMER_NAME = "Sample.Old.Namespace.RenamedEvent";
  private const string CURRENT_NAME = "Sample.New.Namespace.RenamedEvent";

  [Test]
  [Timeout(120000)]
  public async Task Initialize_WhenAFunctionIsOnAnEarlierDefinition_ReappliesItsLastWordAsync(
      CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    // Arrange: the pre-ledger shape of the reconcile function, exactly what a stale database holds.
    // It knows 'updated' and 'drift_detected' but has no former-name branch, so an acknowledged rename
    // reads as drift. Its signature is the canonical one, so the ledger sees nothing to sweep.
    await _execAsync(conn, """
      CREATE OR REPLACE FUNCTION reconcile_message_type_registry(p_entries JSONB)
      RETURNS TABLE (o_action VARCHAR, o_pinned_id UUID, o_clr_type_name VARCHAR, o_stored_clr_type_name VARCHAR) AS $$
      DECLARE
        v_clr VARCHAR;
        v_pinned TEXT;
        v_stored_clr VARCHAR;
      BEGIN
        FOR v_clr, v_pinned IN
          SELECT entry->>'ClrTypeName', NULLIF(entry->>'PinnedId', '') FROM jsonb_array_elements(p_entries) AS entry
        LOOP
          SELECT r.clr_type_name INTO v_stored_clr FROM wh_message_type_registry r WHERE r.pinned_id = v_pinned::uuid;
          o_action := CASE WHEN v_stored_clr = v_clr THEN 'updated' ELSE 'drift_detected' END;
          o_pinned_id := v_pinned::uuid;
          o_clr_type_name := v_clr;
          o_stored_clr_type_name := v_stored_clr;
          RETURN NEXT;
        END LOOP;
      END;
      $$ LANGUAGE plpgsql;
      """, cancellationToken);
    await _execAsync(conn, $"""
      INSERT INTO wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at)
      VALUES ('{FORMER_NAME}', '{PINNED_ID}', 'event', NOW())
      ON CONFLICT (clr_type_name) DO UPDATE SET pinned_id = EXCLUDED.pinned_id;
      """, cancellationToken);

    await Assert.That(await _reconcileActionAsync(conn, cancellationToken)).IsEqualTo("drift_detected")
      .Because("precondition: the stale definition must actually be deployed and blind to former names");

    // Act: an ordinary startup. Every ledger hash matches, there is one overload, so without the
    // stale-definition sweep the fast path skips everything and the function stays stale.
    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    // Assert: the last-word body is back, and the acknowledged rename is adopted rather than warned.
    await Assert.That(await _deployedBodyAsync(conn, "reconcile_message_type_registry", cancellationToken))
      .Contains("v_former_names")
      .Because("the last-word migration (ledger-aware reconcile) must be re-applied over the stale body");
    await Assert.That(await _reconcileActionAsync(conn, cancellationToken)).IsEqualTo("renamed")
      .Because("a stored former name recorded in the ledger is an acknowledged rename, not drift");

    await using var row = conn.CreateCommand();
    row.CommandText = "SELECT clr_type_name FROM wh_message_type_registry WHERE pinned_id = @id";
    row.Parameters.AddWithValue("id", Guid.Parse(PINNED_ID));
    await Assert.That(await row.ExecuteScalarAsync(cancellationToken)).IsEqualTo(CURRENT_NAME)
      .Because("adoption rewrites the registry row old name to current name in place");
  }

  [Test]
  [Timeout(120000)]
  public async Task Initialize_WhenAFrameworkFunctionIsMissing_RecreatesItAsync(
      CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    // Arrange: a framework function vanished (a hand-run DROP, a partial restore); the ledger still says
    // its file is hash-unchanged, so nothing would recreate it.
    await _execAsync(conn, "DROP FUNCTION IF EXISTS reconcile_message_type_registry(JSONB);", cancellationToken);
    await Assert.That(await _overloadCountAsync(conn, "reconcile_message_type_registry", cancellationToken)).IsEqualTo(0)
      .Because("precondition: the function must actually be gone");

    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    await Assert.That(await _overloadCountAsync(conn, "reconcile_message_type_registry", cancellationToken)).IsEqualTo(1)
      .Because("a missing function is the extreme case of a stale one; its last-word file re-runs");
  }

  [Test]
  [Timeout(120000)]
  public async Task Initialize_OnADatabaseThatMatchesItsFiles_DoesNotTriggerTheSweepAsync(
      CancellationToken cancellationToken) {
    // Every framework function on a freshly initialized database must round-trip through the comparison,
    // otherwise the sweep would re-run migrations on every boot. Observable: a second init leaves the
    // ledger untouched (the fast path never reaches the slow path).
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
      .Because("no stale definition: fast path, ledger untouched, nothing re-applied");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task<string> _reconcileActionAsync(NpgsqlConnection conn, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT o_action FROM reconcile_message_type_registry(@entries::jsonb)";
    cmd.Parameters.AddWithValue("entries",
      $$"""[{"ClrTypeName":"{{CURRENT_NAME}}","PinnedId":"{{PINNED_ID}}","Kind":"event","FormerNames":["{{FORMER_NAME}}"]}]""");
    return (string)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task<string> _deployedBodyAsync(NpgsqlConnection conn, string name, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT p.prosrc FROM pg_proc p JOIN pg_namespace n ON p.pronamespace = n.oid
      WHERE p.proname = @name AND n.nspname = 'public'
      """;
    cmd.Parameters.AddWithValue(nameof(name), name);
    return (string)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task<long> _overloadCountAsync(NpgsqlConnection conn, string name, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT COUNT(*) FROM pg_proc p JOIN pg_namespace n ON p.pronamespace = n.oid
      WHERE p.proname = @name AND n.nspname = 'public'
      """;
    cmd.Parameters.AddWithValue(nameof(name), name);
    return (long)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync(ct);
  }
}
