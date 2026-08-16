using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End-to-end cover for the rule that an older build never overwrites a newer one's schema.
/// The applier decides what to run from <c>computed hash != recorded hash</c>, which is symmetric —
/// it cannot say which side is newer — and migration files are edited in place before v1. Without
/// the version guard, an instance from the previous version that restarts after a newer one has
/// migrated re-applies its own older definitions through <c>CREATE OR REPLACE</c> and silently
/// returns the schema to an earlier state beneath the instances still running against it.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationVersionGuard.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class SchemaLedgerVersionTests : EFCoreTestBase {

  /// <summary>A version that outranks whatever this build stamps, so the ledger looks "newer".</summary>
  private const string FUTURE_VERSION = "999.0.0";

  /// <summary>
  /// The SemVer floor — the lowest version that exists (numeric pre-release identifiers rank
  /// below alphanumeric ones). Every stampable build outranks it, INCLUDING CI's PR placeholder
  /// (<c>0.0.0-prNNN.N</c>, used when the version job is skipped on pull requests), which is why
  /// this can't be a small-but-real version like <c>0.0.1</c>: <c>0.0.0-pr478.13 &lt; 0.0.1</c>,
  /// and the guard would (correctly) refuse the re-apply this test needs to see.
  /// </summary>
  private const string ANCIENT_VERSION = "0.0.0-0";

  private async Task<(string FileName, string Hash)> _pickTrackedMigrationAsync(
      NpgsqlConnection conn, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT file_name, content_hash FROM wh_schema_migrations
                        WHERE owner = 'whizbang' ORDER BY file_name LIMIT 1";
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct)) {
      throw new InvalidOperationException("no tracked infrastructure migration to exercise");
    }
    return (reader.GetString(0), reader.GetString(1));
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync(ct);
  }

  [Test]
  [Timeout(120000)]
  public async Task Initialize_WhenLedgerWasWrittenByANewerBuild_DoesNotReapplyOlderContentAsync(
      CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    var (fileName, _) = await _pickTrackedMigrationAsync(conn, cancellationToken);

    // Arrange — the ledger now claims a build far newer than this one applied that migration, and
    // its recorded hash no longer matches what this build computes. That mismatch is exactly what
    // would otherwise drive a re-apply.
    await _execAsync(conn, $@"
      UPDATE wh_schema_versions SET library_version = '{FUTURE_VERSION}'
      WHERE id = (SELECT version_id FROM wh_schema_migrations WHERE file_name = '{fileName}')",
      cancellationToken);
    await _execAsync(conn, $@"
      UPDATE wh_schema_migrations SET content_hash = 'drifted-under-a-newer-build'
      WHERE file_name = '{fileName}'", cancellationToken);

    // Act — this (older) build initializes against that ledger.
    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    // Assert — the row is untouched. Re-applying would have overwritten the hash with this build's
    // own, which is precisely the silent downgrade.
    await using var verify = conn.CreateCommand();
    verify.CommandText = $"SELECT content_hash FROM wh_schema_migrations WHERE file_name = '{fileName}'";
    var after = (string?)await verify.ExecuteScalarAsync(cancellationToken);

    await Assert.That(after).IsEqualTo("drifted-under-a-newer-build")
      .Because("an older build must leave a newer build's ledger row exactly as it found it");
  }

  [Test]
  [Timeout(120000)]
  public async Task Initialize_WhenLedgerWasWrittenByAnOlderBuild_StillAppliesAsync(
      CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    var (fileName, originalHash) = await _pickTrackedMigrationAsync(conn, cancellationToken);

    // Arrange — the same drift, but recorded against a build this one outranks. The ordinary
    // upgrade path must stay open, or the guard would simply freeze every schema forever.
    await _execAsync(conn, $@"
      UPDATE wh_schema_versions SET library_version = '{ANCIENT_VERSION}'
      WHERE id = (SELECT version_id FROM wh_schema_migrations WHERE file_name = '{fileName}')",
      cancellationToken);
    await _execAsync(conn, $@"
      UPDATE wh_schema_migrations SET content_hash = 'drifted-under-an-older-build'
      WHERE file_name = '{fileName}'", cancellationToken);

    // Act
    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    // Assert — re-applied, so the hash is back to what this build computes.
    await using var verify = conn.CreateCommand();
    verify.CommandText = $"SELECT content_hash FROM wh_schema_migrations WHERE file_name = '{fileName}'";
    var after = (string?)await verify.ExecuteScalarAsync(cancellationToken);

    await Assert.That(after).IsEqualTo(originalHash)
      .Because("a newer build must still repair a drifted row written by an older one");
  }
}
