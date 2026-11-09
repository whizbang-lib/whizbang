// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.TestIsolation;

/// <summary>
/// A test that needs the migrated framework schema copies it from the run's template rather than migrating a database
/// of its own from scratch (#2167).
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="EFCoreTestBase"/> test used to create an empty database and run the whole schema pass on it, and
/// that pass was most of what a shard spent its time on. The base now copies <see cref="EFCoreSchemaTemplate"/>, built
/// once. These rules keep it that way: the base never migrates for itself, and a fixture that creates a database and
/// migrates the framework context in it from scratch says why, on a list that only shrinks.
/// </para>
/// <para>
/// What a scan of the source cannot see, <see cref="BaseClassDatabaseOriginTests"/> reads off a live test database: it
/// carries the marker only a copy of the current template has.
/// </para>
/// </remarks>
[Category("Shard1")]
public partial class SchemaTemplateGuardTests {
  /// <summary>
  /// Fixtures that create a database and migrate the framework context in it from scratch, on purpose, relative to the
  /// project and in ordinal order. The list only shrinks.
  /// </summary>
  private static readonly string[] _migrateFromScratchOnPurpose = [
    // Builds and tests the template itself: the from-scratch migration it copies has to happen somewhere.
    "Containers/EFCoreSchemaTemplateTests.cs",
    // The one place the template is migrated, once per run.
    "EFCoreSchemaTemplate.cs",
    // Asserts what the schema pass builds on an empty database, for several contexts; a copy would assert the template.
    "Migrations/ManagedObjectsDeclaredTests.cs",
  ];

  [GeneratedRegex(@"\bPerTestDatabaseFactory\.CreateAsync\(")]
  private static partial Regex _createsAnEmptyDatabase();

  [GeneratedRegex(@"\bnew WorkCoordinationDbContext\(")]
  private static partial Regex _opensTheFrameworkContext();

  [GeneratedRegex(@"\bEnsureWhizbangDatabaseInitializedAsync\(")]
  private static partial Regex _migrates();

  [GeneratedRegex(@"\bEFCoreSchemaTemplate\.CreateDatabaseAsync\(")]
  private static partial Regex _copiesTheTemplate();

  // The build maps source paths (CallerFilePath reads "/_/..." in CI), so the project is found from the test
  // assembly's location instead: walk up to the repository root, then into this project's folder.
  private static string _projectDirectory() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      var candidate = Path.Combine(dir, "tests", "Whizbang.Data.EFCore.Postgres.Tests");
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core")) && Directory.Exists(candidate)) {
        return candidate;
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate the whizbang repository root from AppContext.BaseDirectory");
  }

  private static List<(string Path, string Text)> _sources() {
    var projectDirectory = _projectDirectory();
    var separator = Path.DirectorySeparatorChar;
    return [.. Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
      .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
        && !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
      .Select(path => (Path.GetRelativePath(projectDirectory, path).Replace(separator, '/'), File.ReadAllText(path)))
      .OrderBy(source => source.Item1, StringComparer.Ordinal)];
  }

  private static List<string> _migratingFromScratch() => [.. _sources()
    .Where(source => _createsAnEmptyDatabase().IsMatch(source.Text)
      && _opensTheFrameworkContext().IsMatch(source.Text)
      && _migrates().IsMatch(source.Text))
    .Select(source => source.Path)];

  [Test]
  public async Task TheBaseClass_CopiesTheTemplateAndNeverMigratesForItselfAsync() {
    var baseClass = _sources().Single(source => source.Path == "EFCoreTestBase.cs").Text;

    await Assert.That(_copiesTheTemplate().IsMatch(baseClass)).IsTrue()
      .Because("every base-class test starts from a copy of the migrated template");
    await Assert.That(_migrates().IsMatch(baseClass)).IsFalse()
      .Because("a copy of the template is already migrated; migrating it again per test is the cost #2167 removed");
    await Assert.That(_createsAnEmptyDatabase().IsMatch(baseClass)).IsFalse()
      .Because("an empty database would have to be migrated, test by test");
  }

  [Test]
  public async Task NoFixture_MigratesTheFrameworkSchemaFromScratchWithoutAReasonAsync() {
    var found = _migratingFromScratch();

    await Assert.That(found).Contains("Migrations/ManagedObjectsDeclaredTests.cs")
      .Because("the scan must find a fixture that migrates from scratch on purpose, or an empty answer proves nothing");

    var offenders = found.Except(_migrateFromScratchOnPurpose, StringComparer.Ordinal).ToList();
    await Assert.That(offenders).IsEmpty()
      .Because("these migrate the framework schema in a new database for every test; copy the template with "
        + $"EFCoreSchemaTemplate.CreateDatabaseAsync instead: {string.Join(", ", offenders)}");
  }

  [Test]
  public async Task TheListOfFixturesMigratingFromScratch_OnlyShrinksAsync() {
    var found = _migratingFromScratch();

    var moved = _migrateFromScratchOnPurpose.Except(found, StringComparer.Ordinal).ToList();
    await Assert.That(moved).IsEmpty()
      .Because($"these no longer migrate from scratch, so take them off the list: {string.Join(", ", moved)}");
  }
}

/// <summary>
/// A base-class test's database is a copy of the run's current template: it carries the marker the template was built
/// with, which no database migrated on its own has (#2167).
/// </summary>
[Category("Shard1")]
public class BaseClassDatabaseOriginTests : EFCoreTestBase {
  [Test]
  public async Task ABaseClassTestsDatabase_IsACopyOfTheCurrentTemplateAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var marker = new NpgsqlCommand("SELECT obj_description('public'::regnamespace, 'pg_namespace')", connection);

    await Assert.That((string?)await marker.ExecuteScalarAsync())
      .IsEqualTo(EFCoreSchemaTemplate.MarkerFor(EFCoreSchemaTemplate.Fingerprint))
      .Because("the database has to come from the template built for this run's schema, not a migration of its own");
  }
}
