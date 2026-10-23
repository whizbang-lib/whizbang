// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// A database an earlier release migrated recorded each migration's hash over its raw text. The first start on a
/// release that hashes normalized text rewrites those rows, under the schema lock, before it decides what to run, so
/// nothing is applied again; a row whose file really changed is left alone and applied as any changed file is.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationHashUpgrade.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#what-counts-as-a-change</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class NormalizedMigrationHashUpgradeTests : EFCoreTestBase {
  private const string REWRITE_MESSAGE = "normalized form";

  /// <summary>A logger that keeps what it was told, safe to share between two concurrent initializers.</summary>
  private sealed class RecordingLogger : ILogger {
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();
    public IReadOnlyCollection<string> Messages => _messages;
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      _messages.Enqueue(formatter(state, exception));
    private sealed class Scope : IDisposable { public void Dispose() { } }
  }

  /// <summary>The text each migration's ledger hash is computed over, from the generated initializer itself.</summary>
  private static (string Name, string Text)[] _hashedTexts() =>
    WorkCoordinationDbContextSchemaExtensions.GetHashedMigrationTexts();

  /// <summary>Puts every migration row back on the hash an earlier release recorded: SHA-256 of the raw text.</summary>
  private static async Task<int> _recordLegacyHashesAsync(NpgsqlConnection connection, CancellationToken cancellationToken) {
    var differing = 0;
    foreach (var (name, text) in _hashedTexts()) {
      await using var cmd = new NpgsqlCommand(
        "UPDATE wh_schema_migrations SET content_hash = $1, status_description = 'legacy' WHERE file_name = $2", connection);
      cmd.Parameters.AddWithValue(MigrationSqlNormalizer.LegacyContentHash(text));
      cmd.Parameters.AddWithValue(name);
      var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
      if (rows == 1 && MigrationSqlNormalizer.LegacyContentHash(text) != MigrationSqlNormalizer.ContentHash(text)) {
        differing++;
      }
    }
    return differing;
  }

  private static async Task<Dictionary<string, (string Hash, short Status, string? Description)>> _rowsAsync(
      NpgsqlConnection connection, CancellationToken cancellationToken) {
    var rows = new Dictionary<string, (string, short, string?)>(StringComparer.Ordinal);
    await using var cmd = new NpgsqlCommand(
      "SELECT file_name, content_hash, status, status_description FROM wh_schema_migrations WHERE owner = 'whizbang'", connection);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      rows[reader.GetString(0)] = (reader.GetString(1), reader.GetInt16(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }
    return rows;
  }

  private static int _rewriteReports(params RecordingLogger[] loggers) =>
    loggers.Sum(l => l.Messages.Count(m => m.Contains(REWRITE_MESSAGE, StringComparison.Ordinal)));

  /// <summary>
  /// Every legacy row is rewritten to its normalized hash and nothing is applied again; a second start finds nothing to
  /// rewrite and takes the fast path.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task Upgrade_FromLegacyHashes_RewritesThemAndAppliesNothingAsync(CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(cancellationToken);
    var differing = await _recordLegacyHashesAsync(connection, cancellationToken);
    await Assert.That(differing).IsGreaterThan(0)
      .Because("precondition: shipped migrations carry comments, so their legacy and normalized hashes differ");

    var logger = new RecordingLogger();
    await using (var context = CreateDbContext()) {
      await context.EnsureWhizbangDatabaseInitializedAsync(logger, cancellationToken: cancellationToken);
    }

    var rows = await _rowsAsync(connection, cancellationToken);
    foreach (var (name, text) in _hashedTexts()) {
      await Assert.That(rows[name].Hash).IsEqualTo(MigrationSqlNormalizer.ContentHash(text))
        .Because($"{name}: its legacy hash is rewritten to the normalized one");
      await Assert.That(rows[name].Status).IsNotEqualTo((short)2)
        .Because($"{name}: matching its legacy hash means the file did not change, so it is not applied again");
    }
    await Assert.That(_rewriteReports(logger)).IsEqualTo(1);

    await using var before = new NpgsqlCommand("SELECT MAX(updated_at) FROM wh_schema_migrations", connection);
    var updatedBefore = await before.ExecuteScalarAsync(cancellationToken);
    var second = new RecordingLogger();
    await using (var context = CreateDbContext()) {
      await context.EnsureWhizbangDatabaseInitializedAsync(second, cancellationToken: cancellationToken);
    }
    await using var after = new NpgsqlCommand("SELECT MAX(updated_at) FROM wh_schema_migrations", connection);
    await Assert.That(await after.ExecuteScalarAsync(cancellationToken)).IsEqualTo(updatedBefore)
      .Because("a second start reads normalized hashes, takes the fast path and writes nothing");
    await Assert.That(_rewriteReports(second)).IsEqualTo(0);
  }

  /// <summary>
  /// A row whose hash matches neither the legacy nor the normalized hash of the file as shipped belongs to a file that
  /// really changed: the step leaves it, and the file is applied.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task Upgrade_ARowWhoseFileReallyChanged_IsNotRewrittenAndIsAppliedAsync(CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await _recordLegacyHashesAsync(connection, cancellationToken);
    var (changed, text) = _hashedTexts()[^1];
    await using (var older = new NpgsqlCommand("UPDATE wh_schema_migrations SET content_hash = $1 WHERE file_name = $2", connection)) {
      older.Parameters.AddWithValue(MigrationSqlNormalizer.LegacyContentHash(text + "\nSELECT 'an earlier body';"));
      older.Parameters.AddWithValue(changed);
      await older.ExecuteNonQueryAsync(cancellationToken);
    }

    await using (var context = CreateDbContext()) {
      await context.EnsureWhizbangDatabaseInitializedAsync(new RecordingLogger(), cancellationToken: cancellationToken);
    }

    var rows = await _rowsAsync(connection, cancellationToken);
    await Assert.That(rows[changed].Status).IsEqualTo((short)2)
      .Because("a file that really changed is applied, recorded as an update");
    await Assert.That(rows[changed].Description).StartsWith("Updated from hash");
    await Assert.That(rows[changed].Hash).IsEqualTo(MigrationSqlNormalizer.ContentHash(text));
  }

  /// <summary>Two instances starting together rewrite once: the step runs under the schema lock.</summary>
  [Test]
  [Timeout(180000)]
  public async Task Upgrade_TwoInstancesStartingTogether_RewriteOnceAsync(CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await _recordLegacyHashesAsync(connection, cancellationToken);

    var first = new RecordingLogger();
    var second = new RecordingLogger();
    await using var a = CreateDbContext();
    await using var b = CreateDbContext();
    await Task.WhenAll(
      a.EnsureWhizbangDatabaseInitializedAsync(first, cancellationToken: cancellationToken),
      b.EnsureWhizbangDatabaseInitializedAsync(second, cancellationToken: cancellationToken));

    await Assert.That(_rewriteReports(first, second)).IsEqualTo(1)
      .Because("whichever instance takes the lock first rewrites; the other then finds nothing to rewrite");
    var rows = await _rowsAsync(connection, cancellationToken);
    foreach (var (name, text) in _hashedTexts()) {
      await Assert.That(rows[name].Hash).IsEqualTo(MigrationSqlNormalizer.ContentHash(text));
    }
  }
}
