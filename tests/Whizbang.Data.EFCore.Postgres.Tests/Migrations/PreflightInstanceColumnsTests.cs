// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The pre-migration scripts of Preflight (#1636): the bootstrap closure gives the instance and role tables every
/// column a migration gives them, and the function that records an instance's startup stage, so a fresh database's
/// row records its stage at registration rather than only after the main pass.
/// </summary>
/// <remarks>
/// <para>
/// Read from the SQL that ships, not from a list kept here: the columns from every migration as the provider returns
/// them, and the closure from the generated initializer. A column a later migration adds to one of these tables and
/// not to the closure fails the first test, which is the point: the instance and role tables are Preflight's, and a
/// column added only in the main pass is one the registration and the electors cannot rely on before it.
/// </para>
/// <para>
/// No database: these compare text. What the closure does on a server is
/// <c>SchemaBootstrapPhaseTests.AnEmptyDatabaseRecordsItsStageAtRegistrationAfterTheBootstrapAsync</c>.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/277_PreflightInstanceAndRoleColumns.sql</code-under-test>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
[Category("Migrations")]
[Category("Shard3")]
public sealed partial class PreflightInstanceColumnsTests {
  /// <summary>The tables the registration, the electors, the heartbeat and the eviction fence read and write.</summary>
  private static readonly string[] _instanceAndRoleTables = [
    "wh_service_instances", "wh_instance_evictions", "wh_instance_capabilities", "wh_role_assignments",
    "wh_role_candidates",
  ];

  /// <summary>
  /// Every column any migration adds to an instance or role table is added by the bootstrap closure too, so none of
  /// them waits for the main pass on a fresh database.
  /// </summary>
  [Test]
  public async Task EveryColumnAMigrationAddsToAnInstanceOrRoleTable_IsAddedByTheBootstrapClosureAsync() {
    var migrated = MigrationStepTransactionTests.AllMigrations().SelectMany(m => _addedColumns(m.Sql)).ToHashSet();
    var bootstrapped = WorkCoordinationDbContextSchemaExtensions.GetBootstrapScripts()
      .SelectMany(s => _addedColumns(s.Sql)).ToHashSet();

    await Assert.That(migrated).Contains("wh_service_instances.startup_stage")
      .And.Contains("wh_service_instances.lifecycle_phase").And.Contains("wh_role_assignments.holder_version_key")
      .Because("derived from the shipped files, so the set cannot quietly shrink to nothing");
    await Assert.That(migrated.Except(bootstrapped).Order(StringComparer.Ordinal)).IsEmpty()
      .Because("a column added only in the main pass is one a fresh database's registration cannot write");
  }

  /// <summary>
  /// The closure's definition of the stage recorder is migration 231's, word for word: the bootstrap makes the
  /// function exist earlier and never makes it do something else.
  /// </summary>
  [Test]
  public async Task TheBootstrapsStageRecorder_IsMigration231sDefinitionAsync() {
    var migrations = _migrationsAsTheClosureRendersThem();
    var original = _definitionOf("wh_record_startup_stage", migrations.Single(m => m.Name == "231_StartupStageRecord").Sql);
    var bootstrapped = WorkCoordinationDbContextSchemaExtensions.GetBootstrapScripts()
      .Select(s => _definitionOf("wh_record_startup_stage", s.Sql)).LastOrDefault(b => b is not null);

    await Assert.That(original).IsNotNull();
    await Assert.That(bootstrapped).IsNotNull()
      .Because("registration writes the held stage through this function, so the closure has to create it");
    await Assert.That(bootstrapped).IsEqualTo(original);
  }

  /// <summary>
  /// Every function the bootstrap closure defines, it defines as the last migration to define it does. A closure is
  /// applied again whenever it changes, before the main pass, and a closure carrying an older definition would put
  /// that definition back on every database it reached.
  /// </summary>
  [Test]
  public async Task EveryFunctionTheBootstrapDefines_IsDefinedAsItsLastWordAsync() {
    var lastWord = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var migration in _migrationsAsTheClosureRendersThem()) {
      foreach (var body in MigrationFunctionBodies.Extract(migration.Sql)) {
        lastWord[body.Name] = body.NormalizedBody;
      }
    }
    var bootstrapped = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var script in WorkCoordinationDbContextSchemaExtensions.GetBootstrapScripts()) {
      foreach (var body in MigrationFunctionBodies.Extract(script.Sql)) {
        bootstrapped[body.Name] = body.NormalizedBody;
      }
    }

    await Assert.That(bootstrapped.Keys).Contains("record_capability").And.Contains("wh_vote_role")
      .Because("derived from the shipped closure, so the set cannot quietly shrink to nothing");
    await Assert.That(bootstrapped.Where(b => lastWord[b.Key] != b.Value).Select(b => b.Key)).IsEmpty();
  }

  /// <summary><c>table.column</c> for every column an <c>ALTER TABLE ... ADD COLUMN</c> in <paramref name="sql"/> adds to an instance or role table.</summary>
  private static IEnumerable<string> _addedColumns(string sql) {
    var code = _lineComment().Replace(sql, string.Empty);
    foreach (Match alter in _alterTable().Matches(code)) {
      var table = alter.Groups["table"].Value;
      if (!_instanceAndRoleTables.Contains(table)) {
        continue;
      }
      foreach (Match column in _addColumn().Matches(alter.Groups["body"].Value)) {
        yield return $"{table}.{column.Groups["column"].Value}";
      }
    }
  }

  /// <summary>
  /// Every migration with the schema written as the generated initializer writes it for <c>public</c>: quoted. A body
  /// compared across the two sources then differs only where the definitions do.
  /// </summary>
  private static IReadOnlyList<MigrationScript> _migrationsAsTheClosureRendersThem() =>
    new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "\"public\"").GetMigrations();

  /// <summary>The normalized body of the last definition of <paramref name="function"/> in <paramref name="sql"/>, or null.</summary>
  private static string? _definitionOf(string function, string sql) =>
    MigrationFunctionBodies.Extract(sql).LastOrDefault(b => b.Name == function)?.NormalizedBody;

  [GeneratedRegex(@"--[^\n]*")]
  private static partial Regex _lineComment();

  [GeneratedRegex(@"ALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?(?:""?\w+""?\s*\.\s*)?(?<table>wh_\w+)(?<body>[^;]*);", RegexOptions.IgnoreCase)]
  private static partial Regex _alterTable();

  [GeneratedRegex(@"ADD\s+COLUMN\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<column>\w+)", RegexOptions.IgnoreCase)]
  private static partial Regex _addColumn();
}
