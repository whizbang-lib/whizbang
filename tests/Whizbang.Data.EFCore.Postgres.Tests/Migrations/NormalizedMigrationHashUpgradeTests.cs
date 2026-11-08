// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.TestFixtures;
using Whizbang.Testing.Containers;

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

  private const string CHANGED = "204_DirectInstancesAreLiveByTheirAliveLock.sql";

  /// <summary>
  /// Puts every migration row v0.2623.1 shipped back on the hash that release recorded (captured from its own packages,
  /// never derived from today's text) and returns how many rows it set. The bootstrap runs every start and is left out.
  /// </summary>
  private static async Task<int> _recordLegacyHashesAsync(
      NpgsqlConnection connection, string form, string table, CancellationToken cancellationToken) {
    var set = 0;
    foreach (var (name, hash) in LegacyMigrationLedger.Read(LegacyMigrationLedger.V0_2623_1, form)) {
      if (name.StartsWith("000", StringComparison.Ordinal)) {
        continue;
      }
      set += await _setHashAsync(connection, table, name, hash, cancellationToken);
    }
    return set;
  }

  private static Task<int> _recordLegacyHashesAsync(NpgsqlConnection connection, CancellationToken cancellationToken) =>
    _recordLegacyHashesAsync(connection, "efcore-public", "wh_schema_migrations", cancellationToken);

  private static async Task<int> _setHashAsync(
      NpgsqlConnection connection, string table, string name, string hash, CancellationToken cancellationToken) {
    await using var cmd = new NpgsqlCommand(
      $"UPDATE {table} SET content_hash = $1, status_description = 'legacy' WHERE file_name = $2", connection);
    cmd.Parameters.AddWithValue(hash);
    cmd.Parameters.AddWithValue(name);
    return await cmd.ExecuteNonQueryAsync(cancellationToken);
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
    var set = await _recordLegacyHashesAsync(connection, cancellationToken);
    await Assert.That(set).IsEqualTo(203).Because("precondition: v0.2623.1 shipped 204 migrations, one the bootstrap");

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
    await Assert.That(logger.Messages.Single(m => m.Contains(REWRITE_MESSAGE, StringComparison.Ordinal)))
      .Contains($"{set} migration hash");

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
  /// A row whose hash matches no form of the file as shipped belongs to a file that really changed: v0.2619.0's 204
  /// lacks a statement v0.2623.1's has. The step leaves that row, and the file is applied; no file before it is.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task Upgrade_ARowWhoseFileReallyChanged_IsNotRewrittenAndIsAppliedAsync(CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await _recordLegacyHashesAsync(connection, cancellationToken);
    var (_, olderHash) = LegacyMigrationLedger.Read(LegacyMigrationLedger.V0_2619_0, "efcore-public").Single(r => r.FileName == CHANGED);
    await _setHashAsync(connection, "wh_schema_migrations", CHANGED, olderHash, cancellationToken);

    await using (var context = CreateDbContext()) {
      await context.EnsureWhizbangDatabaseInitializedAsync(new RecordingLogger(), cancellationToken: cancellationToken);
    }

    var rows = await _rowsAsync(connection, cancellationToken);
    await Assert.That(rows[CHANGED].Status).IsEqualTo((short)2)
      .Because("a file that really changed is applied, recorded as an update");
    await Assert.That(rows[CHANGED].Description).StartsWith("Updated from hash");
    var text = _hashedTexts().Single(t => t.Name == CHANGED).Text;
    await Assert.That(rows[CHANGED].Hash).IsEqualTo(MigrationSqlNormalizer.ContentHash(text));
    var updated = rows.Where(r => r.Value.Status == 2).Select(r => r.Key).ToList();
    await Assert.That(updated.All(name => string.CompareOrdinal(name, CHANGED) >= 0)).IsTrue()
      .Because("only the changed file and the later files redefining its objects run; every earlier file is recognized");
  }

  /// <summary>
  /// A context that owns a named schema, upgraded from v0.2623.1: that release qualified bare framework names with an
  /// earlier rule (inside comments and literals too) before hashing, so its recorded hashes are recognized only by
  /// reproducing that rule. None of its migrations is applied again (#2125).
  /// </summary>
  [Test]
  [Timeout(240000)]
  public async Task Upgrade_ANamedSchemaFromV0_2623_1_AppliesNoneOfItsMigrationsAsync(CancellationToken cancellationToken) {
    var database = await PerTestDatabaseFactory.CreateAsync("legacyhashnamed", cancellationToken: cancellationToken);
    try {
      var options = new DbContextOptionsBuilder<NamedSchemaDbContext>()
        .UseNpgsql(database.ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
        .Options;
      await using (var context = new NamedSchemaDbContext(options)) {
        await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
      }
      await using var connection = new NpgsqlConnection(database.ConnectionString);
      await connection.OpenAsync(cancellationToken);
      var table = $"{NamedSchemaDbContext.SCHEMA}.wh_schema_migrations";
      var set = await _recordLegacyHashesAsync(connection, "efcore-named_svc", table, cancellationToken);
      await Assert.That(set).IsEqualTo(203);

      var logger = new RecordingLogger();
      await using (var context = new NamedSchemaDbContext(options)) {
        await context.EnsureWhizbangDatabaseInitializedAsync(logger, cancellationToken: cancellationToken);
      }

      await using var cmd = new NpgsqlCommand(
        $"SELECT file_name, content_hash, status FROM {table} WHERE owner = 'whizbang'", connection);
      var rows = new Dictionary<string, (string Hash, short Status)>(StringComparer.Ordinal);
      await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken)) {
        while (await reader.ReadAsync(cancellationToken)) {
          rows[reader.GetString(0)] = (reader.GetString(1), reader.GetInt16(2));
        }
      }
      foreach (var (name, text) in NamedSchemaDbContextSchemaExtensions.GetHashedMigrationTexts()) {
        await Assert.That(rows[name].Hash).IsEqualTo(MigrationSqlNormalizer.ContentHash(text))
          .Because($"{name}: its v0.2623.1 hash is recognized and rewritten");
        await Assert.That(rows[name].Status).IsNotEqualTo((short)2)
          .Because($"{name}: unchanged apart from its license header, so not applied again");
      }
      await Assert.That(_rewriteReports(logger)).IsEqualTo(1);
    } finally {
      await PerTestDatabaseFactory.DropAsync(database.Name, cancellationToken: CancellationToken.None);
    }
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
