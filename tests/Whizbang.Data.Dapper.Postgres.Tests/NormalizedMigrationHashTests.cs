// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.TestFixtures;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper initializer hashes a migration's normalized text, so an edit that changes nothing executable is skipped,
/// and its first start on a database an earlier release migrated (raw-text hashes) rewrites those rows under the
/// schema lock before it decides what to run, so nothing is applied again.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationHashUpgrade.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#what-counts-as-a-change</docs>
[Category("Integration")]
public class NormalizedMigrationHashTests : IAsyncDisposable {
  private const string REWRITE_MESSAGE = "normalized form";
  private const string TABLE_SQL = "CREATE TABLE IF NOT EXISTS wh_norm_probe (id int);";
  private const string PROBE_SQL = "-- counts how often this file runs\nINSERT INTO wh_norm_probe VALUES (1);";

  private string? _databaseName;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"test_{TrackedGuid.New().Value:N}";
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await admin.ExecuteAsync($"CREATE DATABASE {_databaseName}");
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName
    }.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is null) {
      return;
    }
    try {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
    } catch (NpgsqlException) {
      // The shared container drops leftovers when it stops.
    }
    _databaseName = null;
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private sealed class Provider(params MigrationScript[] migrations) : IMigrationProvider {
    public string Version => "9.9.9-norm";
    public string? ReleaseNotes => null;
    public IReadOnlyList<MigrationScript> GetMigrations() => migrations;
  }

  /// <summary>A logger that keeps what it was told, safe to share between two concurrent initializers.</summary>
  private sealed class RecordingLogger : ILogger {
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();
    public IReadOnlyCollection<string> Messages => _messages;
    public int Rewrites => _messages.Count(m => m.Contains(REWRITE_MESSAGE, StringComparison.Ordinal));
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      _messages.Enqueue(formatter(state, exception));
    private sealed class Scope : IDisposable { public void Dispose() { } }
  }

  private static MigrationScript _bootstrap() =>
    new PostgresMigrationProvider().GetMigrations().First(m => m.Name.StartsWith("000", StringComparison.Ordinal));

  private Task _initAsync(Provider provider, ILogger? logger = null) =>
    new PostgresSchemaInitializer(_connectionString, perspectiveSchemaSql: null, migrationProvider: provider) {
      Logger = logger,
    }.InitializeSchemaAsync();

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    return connection;
  }

  private async Task<long> _probeRunsAsync() {
    await using var connection = await _openAsync();
    return await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_norm_probe");
  }

  private async Task<(string Hash, short Status)> _rowAsync(string name) {
    await using var connection = await _openAsync();
    var row = await connection.QuerySingleAsync<(string, short)>(
      "SELECT content_hash, status FROM wh_schema_migrations WHERE file_name = @name", new { name });
    return row;
  }

  private async Task _setHashAsync(string name, string hash) {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(
      "UPDATE wh_schema_migrations SET content_hash = @hash WHERE file_name = @name", new { name, hash });
  }

  /// <summary>A migration row records the normalized hash of its file.</summary>
  [Test]
  public async Task Initialize_RecordsTheNormalizedHashAsync() {
    await _initAsync(new Provider(_bootstrap(), new("100_table", TABLE_SQL), new("101_probe", PROBE_SQL)));

    await Assert.That((await _rowAsync("101_probe")).Hash).IsEqualTo(MigrationSqlNormalizer.ContentHash(PROBE_SQL));
  }

  /// <summary>
  /// Files whose raw text is already their normalized text have no legacy hash to tell apart, so the rewrite step has
  /// nothing to look for and a second start still skips them.
  /// </summary>
  [Test]
  public async Task Initialize_FilesWithNothingToNormalize_AreSkippedOnTheNextStartAsync() {
    var provider = new Provider(_bootstrap(), new("100_table", TABLE_SQL));
    await _initAsync(provider);

    var logger = new RecordingLogger();
    await _initAsync(provider, logger);

    await Assert.That(await _rowAsync("100_table")).IsEqualTo((MigrationSqlNormalizer.ContentHash(TABLE_SQL), (short)3));
    await Assert.That(logger.Rewrites).IsEqualTo(0);
  }

  /// <summary>A comment-only edit to an applied migration is skipped; an executable edit is applied.</summary>
  [Test]
  public async Task Initialize_ACommentOnlyEditIsSkipped_AnExecutableEditIsAppliedAsync() {
    await _initAsync(new Provider(_bootstrap(), new("100_table", TABLE_SQL), new("101_probe", PROBE_SQL)));
    await Assert.That(await _probeRunsAsync()).IsEqualTo(1L);

    // REUSE-IgnoreStart: a header written into a fixture, not this file's license
    var commented = "/* SPDX-License-Identifier: MIT */\r\n" + PROBE_SQL + "   -- a note\r\n\r\n";
    // REUSE-IgnoreEnd
    await _initAsync(new Provider(_bootstrap(), new("100_table", TABLE_SQL), new("101_probe", commented)));
    await Assert.That(await _probeRunsAsync()).IsEqualTo(1L)
      .Because("a comment, a header and whitespace change nothing executable");
    await Assert.That((await _rowAsync("101_probe")).Status).IsEqualTo((short)3);

    var changed = PROBE_SQL.Replace("VALUES (1)", "VALUES (2)", StringComparison.Ordinal);
    await _initAsync(new Provider(_bootstrap(), new("100_table", TABLE_SQL), new("101_probe", changed)));
    await Assert.That(await _probeRunsAsync()).IsEqualTo(2L)
      .Because("a changed statement is a change");
    await Assert.That((await _rowAsync("101_probe")).Status).IsEqualTo((short)2);
  }

  /// <summary>
  /// A database migrated with raw-text hashes: rows matching the legacy hash of the file as shipped are rewritten and not
  /// applied again, a row matching neither form is applied, and a second start rewrites nothing.
  /// </summary>
  [Test]
  public async Task Upgrade_FromLegacyHashes_RewritesUnchangedFilesAndAppliesChangedOnesAsync() {
    const string OTHER_SQL = "-- another table\nCREATE TABLE IF NOT EXISTS wh_norm_other (id int);";
    var provider = new Provider(
      _bootstrap(), new("100_table", TABLE_SQL), new("101_probe", PROBE_SQL), new("102_other", OTHER_SQL));
    await _initAsync(provider);
    await _setHashAsync("101_probe", MigrationSqlNormalizer.LegacyContentHash(PROBE_SQL));
    await _setHashAsync("102_other", MigrationSqlNormalizer.LegacyContentHash("CREATE TABLE IF NOT EXISTS wh_norm_old (id int);"));

    var logger = new RecordingLogger();
    await _initAsync(provider, logger);

    await Assert.That(await _probeRunsAsync()).IsEqualTo(1L)
      .Because("the probe's row held the legacy hash of the file as shipped, so the file did not change");
    await Assert.That(await _rowAsync("101_probe")).IsEqualTo((MigrationSqlNormalizer.ContentHash(PROBE_SQL), (short)3));
    await Assert.That(await _rowAsync("102_other")).IsEqualTo((MigrationSqlNormalizer.ContentHash(OTHER_SQL), (short)2))
      .Because("a hash matching neither form belongs to a file that really changed, which is applied");
    await Assert.That(logger.Rewrites).IsEqualTo(1);

    var second = new RecordingLogger();
    await _initAsync(provider, second);
    await Assert.That(second.Rewrites).IsEqualTo(0).Because("a second start finds no legacy hash");
    await Assert.That(await _probeRunsAsync()).IsEqualTo(1L);
  }

  /// <summary>The shipped migrations, migrated by an earlier release, apply nothing again on the upgrade start.</summary>
  [Test]
  [Timeout(180000)]
  public async Task Upgrade_TheShippedMigrations_AppliesNothingAgainAsync(CancellationToken cancellationToken) {
    await new PostgresSchemaInitializer(_connectionString).InitializeSchemaAsync(cancellationToken);
    var shipped = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public").GetMigrations()
      .Where(m => !m.Name.StartsWith("000", StringComparison.Ordinal)).ToList();
    foreach (var migration in shipped) {
      await _setHashAsync(migration.Name, MigrationSqlNormalizer.LegacyContentHash(migration.Sql));
    }

    var logger = new RecordingLogger();
    await new PostgresSchemaInitializer(_connectionString) { Logger = logger }.InitializeSchemaAsync(cancellationToken);

    await using var connection = await _openAsync();
    var applied = await connection.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_schema_migrations WHERE status IN (1, 2) AND file_name NOT LIKE 'perspective:%'");
    await Assert.That(applied).IsEqualTo(0L).Because("every row held the legacy hash of its file as shipped");
    foreach (var migration in shipped) {
      await Assert.That((await _rowAsync(migration.Name)).Hash).IsEqualTo(MigrationSqlNormalizer.ContentHash(migration.Sql));
    }
    await Assert.That(logger.Rewrites).IsEqualTo(1);
  }

  /// <summary>The shipped migrations by name, as this release hashes them in the public schema, without the bootstrap.</summary>
  private static Dictionary<string, MigrationScript> _shippedByName() =>
    new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public").GetMigrations()
      .Where(m => !m.Name.StartsWith("000", StringComparison.Ordinal))
      .ToDictionary(m => m.Name, StringComparer.Ordinal);

  /// <summary>
  /// The ledger v0.2623.1 recorded, from its own packages, with the bootstrap left out (it runs every start and is not
  /// change-detected). Its texts differ from today's by the license header alone, which nothing executes.
  /// </summary>
  private static IReadOnlyList<(string FileName, string Hash)> _v0_2623_1Ledger() =>
    [.. LegacyMigrationLedger.Read(LegacyMigrationLedger.V0_2623_1, "dapper-public")
      .Where(row => !row.FileName.StartsWith("000", StringComparison.Ordinal))];

  private async Task<IReadOnlyList<string>> _appliedAsync() {
    await using var connection = await _openAsync();
    return [.. await connection.QueryAsync<string>(
      "SELECT file_name FROM wh_schema_migrations WHERE status IN (1, 2) AND file_name NOT LIKE 'perspective:%' ORDER BY file_name")];
  }

  /// <summary>
  /// A database last migrated by v0.2623.1, the last release before the normalized hash and the license header, holds
  /// the hashes that release recorded. The upgrade start recognizes every one of them, rewrites it, and applies none of
  /// those migrations again (#2125). The hashes were captured from the release's packages, never from today's text,
  /// and none of them equals any hash of today's text, so only real recognition passes.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task Upgrade_FromV0_2623_1_AppliesNoneOfItsMigrationsAndRewritesTheirHashesAsync(CancellationToken cancellationToken) {
    await new PostgresSchemaInitializer(_connectionString).InitializeSchemaAsync(cancellationToken);
    var shipped = _shippedByName();
    var ledger = _v0_2623_1Ledger();
    await Assert.That(ledger.Count).IsEqualTo(203).Because("precondition: v0.2623.1 shipped 204 migrations, one the bootstrap");
    foreach (var (fileName, hash) in ledger) {
      var current = shipped[fileName].Sql;
      await Assert.That(hash).IsNotEqualTo(MigrationSqlNormalizer.ContentHash(current))
        .And.IsNotEqualTo(MigrationSqlNormalizer.LegacyContentHash(current))
        .Because($"precondition: {fileName}'s recorded hash is no hash of today's text");
      await _setHashAsync(fileName, hash);
    }

    var logger = new RecordingLogger();
    await new PostgresSchemaInitializer(_connectionString) { Logger = logger }.InitializeSchemaAsync(cancellationToken);

    await Assert.That(await _appliedAsync()).IsEmpty()
      .Because("every file v0.2623.1 shipped is unchanged apart from its license header, so none is applied again");
    foreach (var (fileName, _) in ledger) {
      await Assert.That((await _rowAsync(fileName)).Hash).IsEqualTo(MigrationSqlNormalizer.ContentHash(shipped[fileName].Sql))
        .Because($"{fileName}: its recorded hash is rewritten to the normalized one");
    }
    await Assert.That(logger.Rewrites).IsEqualTo(1);
    await Assert.That(logger.Messages.Single(m => m.Contains(REWRITE_MESSAGE, StringComparison.Ordinal)))
      .Contains($"{ledger.Count} migration hash");
  }

  /// <summary>
  /// The same upgrade, with one file whose content really changed since the release that recorded it: v0.2619.0's 204
  /// lacks a statement v0.2623.1's has. That file is applied, and nothing before it is.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task Upgrade_FromV0_2623_1_WithAFileThatReallyChanged_AppliesOnlyThatFileAndItsClosureAsync(CancellationToken cancellationToken) {
    const string CHANGED = "204_DirectInstancesAreLiveByTheirAliveLock";
    await new PostgresSchemaInitializer(_connectionString).InitializeSchemaAsync(cancellationToken);
    foreach (var (fileName, hash) in _v0_2623_1Ledger()) {
      await _setHashAsync(fileName, hash);
    }
    var older = LegacyMigrationLedger.Read(LegacyMigrationLedger.V0_2619_0, "dapper-public").Single(r => r.FileName == CHANGED);
    await _setHashAsync(CHANGED, older.Hash);

    await new PostgresSchemaInitializer(_connectionString).InitializeSchemaAsync(cancellationToken);

    var applied = await _appliedAsync();
    await Assert.That(applied).Contains(CHANGED).Because("a file whose content really changed is applied");
    await Assert.That((await _rowAsync(CHANGED)).Status).IsEqualTo((short)2);
    await Assert.That(applied.All(name => string.CompareOrdinal(name, CHANGED) >= 0)).IsTrue()
      .Because("only the changed file and the later files redefining its objects run; every earlier file is recognized");
    await Assert.That(applied).DoesNotContain("087_StreamDigests");
  }

  /// <summary>Two instances starting together rewrite once: the step runs under the schema lock.</summary>
  [Test]
  public async Task Upgrade_TwoInstancesStartingTogether_RewriteOnceAsync() {
    var provider = new Provider(_bootstrap(), new("100_table", TABLE_SQL), new("101_probe", PROBE_SQL));
    await _initAsync(provider);
    await _setHashAsync("101_probe", MigrationSqlNormalizer.LegacyContentHash(PROBE_SQL));

    var first = new RecordingLogger();
    var second = new RecordingLogger();
    await Task.WhenAll(_initAsync(provider, first), _initAsync(provider, second));

    await Assert.That(first.Rewrites + second.Rewrites).IsEqualTo(1);
    await Assert.That(await _probeRunsAsync()).IsEqualTo(1L);
  }

  /// <summary>A rewrite with no logger configured still rewrites, silently.</summary>
  [Test]
  public async Task Upgrade_WithoutALogger_StillRewritesAsync() {
    var provider = new Provider(_bootstrap(), new("100_table", TABLE_SQL), new("101_probe", PROBE_SQL));
    await _initAsync(provider);
    await _setHashAsync("101_probe", MigrationSqlNormalizer.LegacyContentHash(PROBE_SQL));

    await _initAsync(provider);

    await Assert.That(await _rowAsync("101_probe")).IsEqualTo((MigrationSqlNormalizer.ContentHash(PROBE_SQL), (short)3));
    await Assert.That(await _probeRunsAsync()).IsEqualTo(1L);
  }

  /// <summary>Preview reads a legacy row of an unchanged file as a skip, which is what the start would do.</summary>
  [Test]
  public async Task Preview_ALegacyRowOfAnUnchangedFile_IsASkipAsync() {
    var provider = new Provider(_bootstrap(), new("100_table", TABLE_SQL), new("101_probe", PROBE_SQL));
    await _initAsync(provider);
    await _setHashAsync("101_probe", MigrationSqlNormalizer.LegacyContentHash(PROBE_SQL));

    var plan = await new PostgresSchemaInitializer(_connectionString, perspectiveSchemaSql: null, migrationProvider: provider)
      .PreviewAsync();

    var step = plan.Steps.Single(s => s.Name == "101_probe");
    await Assert.That(step.Action).IsEqualTo(MigrationAction.Skip);
    await Assert.That(step.NewHash).IsEqualTo(MigrationSqlNormalizer.ContentHash(PROBE_SQL));
  }
}
