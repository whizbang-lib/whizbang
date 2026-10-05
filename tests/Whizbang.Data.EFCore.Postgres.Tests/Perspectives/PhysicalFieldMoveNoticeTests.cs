// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Data.EFCore.Postgres.Dispatch;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1021: where no document copy exists to fill a column from, or a demoted column holds a type the
/// document cannot, the move is reported once at Warning, naming the rebuild command.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#when-the-document-has-no-copy</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class PhysicalFieldMoveNoticeTests {
  private const string TABLE = PhysicalMoves.TABLE;

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("phys_notice");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await PhysicalMoves.StartAsync(_connectionString);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private Task<string> _scalarAsync(string sql) => PhysicalMoves.ScalarAsync(_connectionString, sql);

  private Task _addRowAsync() => PhysicalMoves.ExecAsync(_connectionString, $$"""
    INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version)
    VALUES (gen_random_uuid(), '{}', '{}', '{}', now(), now(), now(), now(), 1)
    """);

  private async Task<List<string>> _runStepAsync() {
    var logger = new CapturingLogger();
    await using var context = PhysicalMoves.Context(_connectionString);
    var services = new ServiceCollection();
    services.AddSingleton(context);
    services.AddSingleton<IClaimedEmissionStore>(new EFCoreClaimedEmissionStore(context));
    await using var provider = services.BuildServiceProvider();
    await new PhysicalColumnFillMaintenanceStep(typeof(PhysicalMovesDbContext), logger, new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 3, 0, TimeSpan.Zero)))
      .RunAsync(provider, CancellationToken.None);
    return logger.Warnings;
  }

  /// <summary>A column promoted on a table with rows, which the document cannot fill, is reported once.</summary>
  [Test]
  public async Task AColumnTheDocumentCannotFill_IsReportedForARebuildAsync() {
    await _addRowAsync();

    var outcome = await _scalarAsync($"SELECT wh_arm_physical_column('\"public\".{TABLE}', 'shape', 'Shape', NULL, false)");

    await Assert.That(outcome).IsEqualTo("rebuild");
    var warnings = await _runStepAsync();
    await Assert.That(warnings).Count().IsEqualTo(1);
    await Assert.That(warnings[0]).Contains($"public.{TABLE}.shape").And.Contains(PhysicalColumnFill.REBUILD_COMMAND);
    await Assert.That(await _runStepAsync()).IsEmpty().Because("a notice is taken once");
  }

  /// <summary>On a table with no rows there is nothing to restore, so nothing is reported; a table not there yet arms nothing.</summary>
  [Test]
  public async Task AColumnTheDocumentCannotFill_OnAnEmptyOrMissingTable_IsNotReportedAsync() {
    await Assert.That(await _scalarAsync($"SELECT wh_arm_physical_column('\"public\".{TABLE}', 'shape', 'Shape', NULL, false)"))
      .IsEqualTo("empty");
    await Assert.That(await _scalarAsync("SELECT wh_arm_physical_column('\"public\".wh_per_not_yet', 'shape', 'Shape', NULL, false)"))
      .IsEqualTo("absent");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_physical_column_fills WHERE direction <> 'recorded'")).IsEqualTo("0");
  }

  /// <summary>A demoted column the framework recorded, of a type the document cannot hold, is reported, and its values are left where they are.</summary>
  [Test]
  public async Task ADemotedColumnTheDocumentCannotHold_IsReportedForARebuildAsync() {
    await PhysicalMoves.ExecAsync(_connectionString, $$"""
      ALTER TABLE {{TABLE}} ADD COLUMN spot point;
      INSERT INTO wh_physical_column_fills (table_name, column_name, json_key, extraction, direction, armed_at, settled_at)
      VALUES ('public.{{TABLE}}', 'spot', 'Spot', NULL, 'recorded', now(), now());
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version, spot)
      VALUES (gen_random_uuid(), '{}', '{}', '{}', now(), now(), now(), now(), 1, point(1, 2));
      """);
    await PhysicalMoves.ForgetSchemaAsync(_connectionString);
    await PhysicalMoves.StartAsync(_connectionString);

    await Assert.That(await _scalarAsync("SELECT direction FROM wh_physical_column_fills WHERE column_name = 'spot'")).IsEqualTo("rebuild");
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE data ? 'Spot'")).IsEqualTo("0");
    var warnings = await _runStepAsync();
    await Assert.That(warnings).Count().IsEqualTo(1);
    await Assert.That(warnings[0]).Contains("Spot");
  }

  private sealed class CapturingLogger : ILogger<PhysicalColumnFillMaintenanceStep> {
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        Warnings.Add(formatter(state, exception));
      }
    }
  }
}
