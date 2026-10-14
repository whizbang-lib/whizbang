// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #1351: every work-table <c>VACUUM</c> runs with the table owner's rights. Postgres does not refuse a
/// non-owner's <c>VACUUM</c>: it warns, skips the table and reports success. So each case is judged by
/// <c>vacuum_count</c>, which counts manual vacuums only, never by the absence of an error.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class WorkTableVacuumTests : EFCoreTestBase {
  private static readonly string[] _workTables = ["wh_outbox", "wh_inbox", "wh_perspective_events", "wh_message_deduplication"];

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<Dictionary<string, long>> _vacuumCountsAsync(CancellationToken cancellationToken) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync(cancellationToken);
    await using (var flush = new NpgsqlCommand("SELECT pg_stat_force_next_flush()", db)) {
      await flush.ExecuteNonQueryAsync(cancellationToken);
    }
    await using var read = new NpgsqlCommand(
      "SELECT relname, vacuum_count FROM pg_stat_user_tables WHERE schemaname = 'public' AND relname = ANY(@t)", db);
    read.Parameters.AddWithValue("t", _workTables);
    var counts = new Dictionary<string, long>(StringComparer.Ordinal);
    await using var reader = await read.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      counts[reader.GetString(0)] = reader.GetInt64(1);
    }
    return counts;
  }

  private static async Task _assertEachVacuumedAsync(Dictionary<string, long> before, Dictionary<string, long> after, long by) {
    foreach (var table in _workTables) {
      await Assert.That(after[table] - before[table]).IsEqualTo(by).Because($"{table}'s vacuum_count");
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task OnAnOwnerConnection_EveryWorkTableIsVacuumedAsync(CancellationToken cancellationToken) {
    await using var context = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());
    var before = await _vacuumCountsAsync(cancellationToken);

    var outcomes = await coordinator.VacuumWorkTablesAsync(cancellationToken);

    await Assert.That(outcomes.Select(o => o.TableName)).IsEquivalentTo(_workTables);
    await Assert.That(outcomes.All(o => o.Vacuumed && o.SkippedReason is null)).IsTrue();
    await _assertEachVacuumedAsync(before, await _vacuumCountsAsync(cancellationToken), by: 1);
  }

  /// <summary>
  /// Under a data-only application role: with <c>-init</c> configured the init connection, which owns the
  /// tables, vacuums them; without it nothing is vacuumed and each table comes back with the reason, naming
  /// the table and the role. The second case is what shows the role really lacks the rights.
  /// </summary>
  [Test]
  [Timeout(120000)]
  [Arguments(true)]
  [Arguments(false)]
  public async Task UnderADataOnlyApplicationRole_OnlyTheInitConnectionVacuumsAsync(
      bool withInitConnection, CancellationToken cancellationToken) {
    var role = $"wh_app_{Guid.NewGuid():N}"[..30];
    await _execAsync($"""
      CREATE ROLE {role} LOGIN PASSWORD 'data-only';
      GRANT USAGE ON SCHEMA public TO {role};
      GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role};
      GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO {role};
      GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO {role};
      """);
    try {
      var applicationConnection = new NpgsqlConnectionStringBuilder(ConnectionString) {
        Username = role,
        Password = "data-only",
      }.ConnectionString;
      var services = new ServiceCollection();
      if (withInitConnection) {
        services.AddKeyedSingleton<SchemaInitConnection>(typeof(WorkCoordinationDbContext), new SchemaInitConnection(ConnectionString));
      }
      await using var provider = services.BuildServiceProvider();
      // As AddDbContext does: the context carries the application's services.
      await using var applicationContext = new WorkCoordinationDbContext(
        new DbContextOptionsBuilder<WorkCoordinationDbContext>()
          .UseNpgsql(applicationConnection)
          .UseApplicationServiceProvider(provider)
          .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
          .Options);
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        applicationContext, JsonContextRegistry.CreateCombinedOptions());
      var before = await _vacuumCountsAsync(cancellationToken);

      var outcomes = await coordinator.VacuumWorkTablesAsync(cancellationToken);

      var after = await _vacuumCountsAsync(cancellationToken);
      if (withInitConnection) {
        await Assert.That(outcomes.All(o => o.Vacuumed)).IsTrue();
        await _assertEachVacuumedAsync(before, after, by: 1);
      } else {
        await Assert.That(outcomes.Count).IsEqualTo(_workTables.Length);
        foreach (var outcome in outcomes) {
          await Assert.That(outcome.Vacuumed).IsFalse();
          await Assert.That(outcome.SkippedReason!).Contains($"role '{role}' does not own {outcome.TableName}");
        }
        await _assertEachVacuumedAsync(before, after, by: 0);
      }
    } finally {
      await _execAsync($"DROP OWNED BY {role}; DROP ROLE {role};");
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task AWorkTableThatDoesNotExist_IsReportedNotVacuumedAsync(CancellationToken cancellationToken) {
    await _execAsync("DROP TABLE wh_message_deduplication CASCADE");
    await using var context = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());

    var outcomes = await coordinator.VacuumWorkTablesAsync(cancellationToken);

    var missing = outcomes.Single(o => o.TableName == "wh_message_deduplication");
    await Assert.That(missing.Vacuumed).IsFalse();
    await Assert.That(missing.SkippedReason!).Contains("does not exist");
    await Assert.That(outcomes.Count(o => o.Vacuumed)).IsEqualTo(3);
  }
}
