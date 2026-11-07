// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A service starting against a database an earlier release migrated names each perspective whose model is recursive,
/// once, at Warning with a stable event id, because rows that release wrote may have kept only the first level of the
/// recursion (owner decision on #2112). Nothing is rebuilt automatically.
/// </summary>
/// <remarks>
/// <para>
/// "An earlier release" is read from the migration ledger: migration <c>351_RecursiveDocumentsStoredWhole</c> ships with
/// the fix and records nothing but its own ledger row, so a ledger without it was last migrated by a release before the
/// fix. The check runs before this start's migrations apply, so it reads the previous release's ledger.
/// </para>
/// <para>
/// Silent on a fresh database (no ledger, or no perspective table yet), on a database already at or after the fix (the
/// row is there), and for a perspective whose table holds no rows (nothing was written, so nothing was truncated).
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild</docs>
[Category("Integration")]
[Category("Shard4")]
public class RecursivePerspectiveRebuildNoticeTests {
  private static readonly string[] _recursive = [
    "Whizbang.Data.EFCore.Postgres.Tests.ArrayTreeProjection",
    "Whizbang.Data.EFCore.Postgres.Tests.ImmutableTreeProjection",
    "Whizbang.Data.EFCore.Postgres.Tests.InterfaceTreeProjection",
    "Whizbang.Data.EFCore.Postgres.Tests.ListTreeProjection",
    "Whizbang.Data.EFCore.Postgres.Tests.MutualTreeProjection",
  ];

  private static readonly string[] _tables = [
    "wh_per_array_tree_document",
    "wh_per_immutable_tree_document",
    "wh_per_interface_tree_document",
    "wh_per_list_tree_document",
    "wh_per_mutual_tree_document",
  ];

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("rebuild_note");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private RecursiveModelsDbContext _context() {
    ModelRegistrationRegistry.InvokeRegistration(new ServiceCollection(), typeof(RecursiveModelsDbContext), new PostgresUpsertStrategy());
    return new RecursiveModelsDbContext(new DbContextOptionsBuilder<RecursiveModelsDbContext>()
      .UseNpgsql(_connectionString)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);
  }

  /// <summary>Initializes the schema as a starting service does, with a logger that keeps what it is told.</summary>
  private async Task<RecordingLogger> _startAsync() {
    var logger = new RecordingLogger();
    await using var context = _context();
    await context.EnsureWhizbangDatabaseInitializedAsync(logger, _connectionString);
    return logger;
  }

  private static IReadOnlyList<string> _notices(RecordingLogger logger) =>
    [.. logger.Entries.Where(e => e.EventId.Id == RecursiveDocumentRebuildNotice.EVENT_ID).Select(e => e.Message)];

  private async Task _executeAsync(string sql) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>A row in every recursive perspective's table, as an earlier release would have written one.</summary>
  private async Task _writeRowsAsync() {
    foreach (var table in _tables) {
      await _executeAsync(
        $"INSERT INTO {table} (id, data, metadata, scope, created_at, updated_at, version) "
        + $"VALUES ('{TrackedGuid.New()}', '{{}}', '{{}}', '{{}}', now(), now(), 1)");
    }
  }

  /// <summary>The ledger as a release before the fix left it: everything but the marker migration's row.</summary>
  private Task _forgetTheFixAsync() =>
    _executeAsync($"DELETE FROM wh_schema_migrations WHERE file_name = '{RecursiveDocumentRebuildNotice.MARKER_MIGRATION}'");

  /// <summary>The upgrade the notice exists for: each recursive perspective is named once, at Warning.</summary>
  [Test]
  public async Task AnUpgradeFromAReleaseBeforeTheFix_NamesEachRecursivePerspectiveOnceAsync() {
    await _startAsync();
    await _writeRowsAsync();
    await _forgetTheFixAsync();

    var logger = await _startAsync();

    var notices = _notices(logger);
    await Assert.That(notices).Count().IsEqualTo(_recursive.Length)
      .Because("one line per recursive perspective, and only those");
    foreach (var perspective in _recursive) {
      var notice = notices.Single(n => n.Contains($"'{perspective}'", StringComparison.Ordinal));
      await Assert.That(notice).Contains("#2112");
      await Assert.That(notice).Contains("first level");
      await Assert.That(notice).Contains("rebuilt from its events");
    }
    await Assert.That(logger.Entries.Where(e => e.EventId.Id == RecursiveDocumentRebuildNotice.EVENT_ID)
      .All(e => e.Level == LogLevel.Warning && e.EventId.Name == RecursiveDocumentRebuildNotice.EVENT_NAME)).IsTrue();
  }

  /// <summary>Once: the start that upgrades records the fix, so the next start is silent.</summary>
  [Test]
  public async Task TheStartAfterTheUpgrade_IsSilentAsync() {
    await _startAsync();
    await _writeRowsAsync();
    await _forgetTheFixAsync();
    _ = await _startAsync();

    var again = await _startAsync();

    await Assert.That(_notices(again)).IsEmpty();
  }

  /// <summary>A fresh database has no rows any earlier release wrote.</summary>
  [Test]
  public async Task AFreshDatabase_IsSilentAsync() {
    var logger = await _startAsync();

    await Assert.That(_notices(logger)).IsEmpty();
  }

  /// <summary>A database already at the fix wrote its rows whole.</summary>
  [Test]
  public async Task ADatabaseAlreadyAtTheFix_IsSilentAsync() {
    await _startAsync();
    await _writeRowsAsync();

    var logger = await _startAsync();

    await Assert.That(_notices(logger)).IsEmpty();
  }

  /// <summary>A perspective whose table is empty had nothing written, so nothing was truncated.</summary>
  [Test]
  public async Task AnUpgradeOverEmptyTables_IsSilentAsync() {
    await _startAsync();
    await _forgetTheFixAsync();

    var logger = await _startAsync();

    await Assert.That(_notices(logger)).IsEmpty();
  }

  /// <summary>The check itself, without a start: no ledger at all is a database nothing has migrated.</summary>
  [Test]
  public async Task NoLedger_ReportsNothingAsync() {
    var logger = new RecordingLogger();

    var reported = await RecursiveDocumentRebuildNotice.ReportAsync(
      () => new NpgsqlConnection(_connectionString), "public", [("App.TreePerspective", "wh_per_tree")], logger, CancellationToken.None);

    await Assert.That(reported).IsEmpty();
    await Assert.That(logger.Entries).IsEmpty();
  }

  /// <summary>Nothing recursive to name: the database is not even opened.</summary>
  [Test]
  public async Task NoRecursivePerspective_DoesNotOpenTheDatabaseAsync() {
    var reported = await RecursiveDocumentRebuildNotice.ReportAsync(
      () => throw new InvalidOperationException("must not connect"), "public", [], null, CancellationToken.None);

    await Assert.That(reported).IsEmpty();
  }

  /// <summary>A pre-fix ledger names the perspective even without a logger to tell, and skips a table not created.</summary>
  [Test]
  public async Task APreFixLedger_ReportsPerspectivesWithRowsOnlyAsync() {
    await _startAsync();
    await _writeRowsAsync();
    await _forgetTheFixAsync();

    var reported = await RecursiveDocumentRebuildNotice.ReportAsync(
      () => new NpgsqlConnection(_connectionString), "public",
      [("App.ListTree", "wh_per_list_tree_document"), ("App.Gone", "wh_per_no_such_table")], null, CancellationToken.None);

    await Assert.That(reported).IsEquivalentTo(["App.ListTree"]);
  }

  private sealed class RecordingLogger : ILogger {
    public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (Entries) {
        Entries.Add((logLevel, eventId, formatter(state, exception)));
      }
    }
  }
}
