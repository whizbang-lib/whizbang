// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.TestIsolation;

/// <summary>
/// A fixture creates its scratch database through <c>PerTestDatabaseFactory</c> rather than issuing its own
/// <c>CREATE DATABASE</c>, so the database's creation, the work that fills it and its drop follow one policy for a
/// container every test process on the machine shares (#1940, #1941).
/// </summary>
/// <remarks>
/// <para>
/// A statement that sets a test up does as much work under load as it does on an idle machine; only its duration
/// changes. Two fixtures issued theirs on a connection with Npgsql's default 30-second command timeout, so when another
/// shard was loading the same container the client cancelled them: the containment fixture's 200,000-row seed (the
/// server logged <c>canceling statement due to user request</c> on the insert, then the fixture's own teardown ending
/// the session), and the vector fixture's <c>CREATE DATABASE</c> and <c>DROP DATABASE</c>. The tests failed before an
/// assertion ran.
/// </para>
/// <para>
/// The fixtures listed in <see cref="_notYetThroughTheFactory"/> predate the factory. The list only shrinks: a fixture
/// that moves to the factory has to leave it, and a new fixture that issues its own statement fails here.
/// </para>
/// </remarks>
[Category("Shard1")]
public partial class ScratchDatabaseLifecycleGuardTests {
  /// <summary>
  /// Fixtures that still issue their own <c>CREATE DATABASE</c>, relative to the project and in ordinal order.
  /// </summary>
  private static readonly string[] _notYetThroughTheFactory = [
    "AtomicUpsertPhysicalFieldsIntegrationTests.cs",
    "BranchCoverage/SecondPass/CollectiveAdapterNullAndSchemaTests.cs",
    "ColdBootJourneyE2ETests.cs",
    "Collective/CollectiveDispatcherEFCoreIntegrationTests.cs",
    "Collective/CollectiveOrderingIntegrationTests.cs",
    "Collective/CollectivePhysicalColumnIntegrationTests.cs",
    "ComplexTypeJsonMappingTests.cs",
    // Creates a database under a name the factory would not give, so the sweep has something to find.
    "Containers/PerTestDatabaseSweepTests.cs",
    "DbContextWithoutPerspectivesTests.cs",
    "EFCoreTestBase.cs",
    "FullLinqSupportTests.cs",
    "Migrations/CanonicalTemporalFunctionTests.cs",
    "Migrations/FleetVersionsTests.cs",
    "MultiPerspectivePostgresUpsertSymmetryTests.cs",
    "OpaqueDocumentRoundTripTests.cs",
    "OrderSchemaEvolutionComplexTypeTests.cs",
    "PerspectiveModelComplexTypesTests.cs",
    "Perspectives/CanonicalTemporalRewriteIntegrationTests.cs",
    "Perspectives/LengthConstraintDdlTests.cs",
    "Perspectives/SplitModeIntegrationTests.cs",
    "Perspectives/SplitModeProductionTests.cs",
    "Perspectives/TableStatisticsProviderTests.cs",
    "PhysicalColumnBackfillIntegrationTests.cs",
    "PhysicalFieldIntegrationTests.cs",
    "QueryTranslation/CanonicalTemporalStorageTests.cs",
    "QueryTranslation/ContainmentTypeEligibilityProbeTests.cs",
    "QueryTranslation/JsonIndexUsageTests.cs",
    "QueryTranslation/PerspectiveDateFormatLockTests.cs",
    "ReconcileConvergenceE2ETests.cs",
    "StartupParity/StartupParityTests.cs",
    "UnifiedQuerySyntaxTests.cs",
    "VectorSearchIntegrationTests.cs",
  ];

  /// <summary>A <c>CREATE DATABASE</c> the fixture runs itself: an interpolated statement naming the database.</summary>
  [GeneratedRegex(@"\$""CREATE DATABASE ")]
  private static partial Regex _createsADatabase();

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

  private static List<string> _fixturesThatCreateTheirOwnDatabase() {
    var projectDirectory = _projectDirectory();
    var separator = Path.DirectorySeparatorChar;
    return [.. Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
      .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
        && !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
        && _createsADatabase().IsMatch(File.ReadAllText(path)))
      .Select(path => Path.GetRelativePath(projectDirectory, path).Replace(separator, '/'))
      .Order(StringComparer.Ordinal)];
  }

  [Test]
  public async Task EveryFixtureCreatesItsScratchDatabaseThroughTheFactoryAsync() {
    var found = _fixturesThatCreateTheirOwnDatabase();

    await Assert.That(found).Contains("EFCoreTestBase.cs")
      .Because("the scan must find the statement the base fixture issues, or an empty answer proves nothing");

    var offenders = found.Except(_notYetThroughTheFactory, StringComparer.Ordinal).ToList();
    await Assert.That(offenders).IsEmpty()
      .Because("these issue their own CREATE DATABASE on a connection with the default command timeout, so a busy "
        + $"shared container cancels them; create the database with PerTestDatabaseFactory: {string.Join(", ", offenders)}");
  }

  [Test]
  public async Task TheListOfFixturesNotYetThroughTheFactoryOnlyShrinksAsync() {
    var found = _fixturesThatCreateTheirOwnDatabase();

    var moved = _notYetThroughTheFactory.Except(found, StringComparer.Ordinal).ToList();
    await Assert.That(moved).IsEmpty()
      .Because($"these no longer issue their own CREATE DATABASE, so take them off the list: {string.Join(", ", moved)}");
  }
}
