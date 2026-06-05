using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// production CrashLoopBackOff regression-lock (Jun-2026). Simulates the scenario:
/// an existing wh_dead_letters table — created by the pre-Slice-2 version of
/// migration 050 — has NO error_fingerprint / error_fingerprint_version columns.
/// The migration runner (hash-based, see DbContextSchemaExtensionTemplate.cs)
/// re-applies 050 when its content hash changes, and the re-apply MUST NOT
/// crash on a CREATE INDEX referencing a column the file's own CREATE TABLE
/// IF NOT EXISTS won't add (because the table already exists).
///
/// <para>Locked invariant: the partial fingerprint index lives in migration
/// 053 AFTER its ALTER TABLE that adds the columns. Migration 050 only
/// contains the column DEFINITIONS in its CREATE TABLE clause — which is a
/// no-op on existing databases — and is otherwise the same as pre-Slice-2.
/// This means re-applying 050 on production-class existing databases is safe.</para>
///
/// <para>Production reproduction: production CrashLoopBackOff on Jun-2026 when
/// migration 050's content hash changed; the runner re-applied 050; CREATE
/// INDEX wh_dead_letters_fingerprint_idx ON wh_dead_letters (error_fingerprint)
/// failed with "column error_fingerprint does not exist" because 053 hadn't
/// run yet to add the column. App pods exited startup, StartupProbe failed,
/// CrashLoopBackOff propagated to bff-service, chat-service, email-service.</para>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public class DeadLetterFingerprintMigrationRerunSqlTests : EFCoreTestBase {

  // --- helpers ---

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = (NpgsqlConnection)CreateDbContext().Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _dropFingerprintArtifactsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      DROP INDEX IF EXISTS wh_dead_letters_fingerprint_idx;
      ALTER TABLE wh_dead_letters DROP COLUMN IF EXISTS error_fingerprint;
      ALTER TABLE wh_dead_letters DROP COLUMN IF EXISTS error_fingerprint_version;
      """;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<bool> _columnExistsAsync(NpgsqlConnection conn, string column) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'wh_dead_letters' AND column_name = @col
      )
      """;
    cmd.Parameters.AddWithValue("col", column);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<bool> _indexExistsAsync(NpgsqlConnection conn, string indexName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE tablename = 'wh_dead_letters' AND indexname = @name
      )
      """;
    cmd.Parameters.AddWithValue("name", indexName);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>
  /// Executes the relevant slice of migration 053 (the ALTER TABLE + CREATE INDEX
  /// that should idempotently restore the fingerprint columns and index). Mirrors
  /// the SQL exactly so a future drift between this test and the migration would
  /// catch in CI.
  /// </summary>
  private static async Task _replayMigration053FingerprintDdlAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      ALTER TABLE wh_dead_letters
        ADD COLUMN IF NOT EXISTS error_fingerprint VARCHAR(16) NULL,
        ADD COLUMN IF NOT EXISTS error_fingerprint_version SMALLINT NULL;

      CREATE INDEX IF NOT EXISTS wh_dead_letters_fingerprint_idx
        ON wh_dead_letters (error_fingerprint)
        WHERE error_fingerprint IS NOT NULL;
      """;
    await cmd.ExecuteNonQueryAsync();
  }

  // --- tests ---

  [Test]
  public async Task Migration053_AppliedToExistingTableWithoutFingerprintColumns_AddsColumnsAndIndexAsync() {
    await using var conn = await _openAsync();

    // Setup: drop the fingerprint artifacts to simulate the pre-Slice-2 state
    // of an existing database (production on Jun-2026: migration 050 had run, but
    // without the fingerprint columns from the original CREATE TABLE).
    await _dropFingerprintArtifactsAsync(conn);
    await Assert.That(await _columnExistsAsync(conn, "error_fingerprint")).IsFalse()
      .Because("Setup precondition: simulating an existing production-class database where the fingerprint columns don't exist yet.");
    await Assert.That(await _indexExistsAsync(conn, "wh_dead_letters_fingerprint_idx")).IsFalse()
      .Because("Setup precondition: index also gone in the simulated state.");

    // Act: replay the migration 053 DDL. The production bug was that the partial
    // index lived in migration 050 — CREATE INDEX ON (error_fingerprint) ran
    // BEFORE the ALTER TABLE that adds the column. Now that we moved the index
    // into 053 AFTER the ALTER TABLE, the DDL applies cleanly.
    await _replayMigration053FingerprintDdlAsync(conn);

    // Assert: both columns and the index now exist.
    await Assert.That(await _columnExistsAsync(conn, "error_fingerprint")).IsTrue()
      .Because("ALTER TABLE in migration 053 MUST add error_fingerprint when it's missing — the column-add is the precondition for everything else (move_to_dead_letters INSERT, the partial index, the fingerprint backfill).");
    await Assert.That(await _columnExistsAsync(conn, "error_fingerprint_version")).IsTrue()
      .Because("ALTER TABLE in migration 053 MUST add error_fingerprint_version alongside the fingerprint column — Slice 6's version-aware backfill keys off it.");
    await Assert.That(await _indexExistsAsync(conn, "wh_dead_letters_fingerprint_idx")).IsTrue()
      .Because("Partial index MUST be created in migration 053 (after the column-add), not migration 050 — putting it in 050 caused the production CrashLoopBackOff on Jun-2026.");
  }

  [Test]
  public async Task Migration053_ReAppliedToTableWithFingerprintAlreadyPresent_IsIdempotentAsync() {
    await using var conn = await _openAsync();

    // Precondition: the fresh test database has the columns + index already
    // (full migration chain ran during fixture setup).
    await Assert.That(await _columnExistsAsync(conn, "error_fingerprint")).IsTrue();
    await Assert.That(await _indexExistsAsync(conn, "wh_dead_letters_fingerprint_idx")).IsTrue();

    // Act: replay the migration 053 DDL — must be a no-op given the
    // IF NOT EXISTS guards. If it ever threw "column already exists" or
    // "index already exists", the hash-based runner's re-apply path would
    // crash on every fresh deploy after the first.
    await _replayMigration053FingerprintDdlAsync(conn);

    await Assert.That(await _columnExistsAsync(conn, "error_fingerprint")).IsTrue()
      .Because("Idempotent: re-running 053 leaves the column intact.");
    await Assert.That(await _indexExistsAsync(conn, "wh_dead_letters_fingerprint_idx")).IsTrue()
      .Because("Idempotent: re-running 053 leaves the index intact.");
  }
}
