// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.TestIsolation;

/// <summary>
/// This project's tests run in parallel by default, and a test that serializes says what it shares and why (#2069).
/// </summary>
/// <remarks>
/// <para>
/// Every class once ran one at a time under one key, <c>EFCorePostgresTests</c>, inherited from the base class with
/// the reason "the tests compete for the shared container's connections", although each test creates a database of
/// its own. An EF Core shard was the last suite to finish in every CI run. The key went; what the tests really share
/// is the container's capacity, and the project's parallel limit is sized to it.
/// </para>
/// <para>
/// These rules keep it that way: a new test is parallel unless it says otherwise, and saying otherwise takes a
/// reason on the attribute's line. <c>ai-docs/efcore-test-parallelism.md</c> has the classification.
/// </para>
/// </remarks>
[Category("Shard1")]
public partial class ParallelismPolicyGuardTests {
  /// <summary>The key every class once inherited from the base class.</summary>
  private const string RETIRED_KEY = "EFCorePostgresTests";

  /// <summary>
  /// The connections one test may hold at its peak: the per-test data source, the connections the test opens itself,
  /// the workers it starts, and the admin connection that creates and drops its database.
  /// </summary>
  /// <remarks>
  /// Sampled from <c>pg_stat_activity</c> every second through full runs of every shard on the build servers: most
  /// test databases hold 2 to 6 connections, a few hosts with every worker running reach 60, and two shards sharing
  /// one container never held more than 88 between them. Twenty per test is the sum's budget, not any one test's
  /// ceiling (see <c>ai-docs/efcore-test-parallelism.md</c>).
  /// </remarks>
  internal const int CONNECTIONS_PER_TEST = 20;

  /// <summary>
  /// The test processes that share one container at once: two build-server slots on one server. A CI shard runs
  /// alone on its runner.
  /// </summary>
  internal const int PROCESSES_PER_CONTAINER = 2;

  /// <summary>
  /// Connections the server keeps for itself and for the setup of other test projects on the same machine.
  /// </summary>
  internal const int HEADROOM_CONNECTIONS = 100;

  /// <summary>
  /// Files whose serialization predates the reason rule. The list only shrinks: give the attribute its reason and
  /// take the file off.
  /// </summary>
  private static readonly string[] _predateTheReasonRule = [
    "AtMostOnceOccurrenceIntegrityTests.cs",
    "AuditRebuildRequestReceptorTests.cs",
    "BitemporalColumnMigrationTests.cs",
    "BitemporalWritePathTests.cs",
    "ConsumedTypeRegistryTests.cs",
    "DiscardPendingMessagesSqlTests.cs",
    "EffectiveExpiryLadderTests.cs",
    "EnrolledRowReaperSqlTests.cs",
    "EvictionCompanionCleanupSqlTests.cs",
    "IntegrityAuditClaimTests.cs",
    "IntegrityCheckpointAdvanceLostRaceTests.cs",
    "IntegrityCheckpointAdvanceTests.cs",
    "MultiStream/MultiStreamCommandEndToEndTests.cs",
    "OffloadClaimLedgerSqlTests.cs",
    "OpaqueDocumentRoundTripTests.cs",
    "Partitioning/SignalPartitionCarryForwardTests.cs",
    "Partitioning/SignalPartitionConversionTests.cs",
    "PerspectiveDocumentSerializationTests.cs",
    "PerspectiveRetentionEnrollmentSqlTests.cs",
    "PerspectiveRowCapSqlTests.cs",
    "PgDurableSignalRetentionWindowTests.cs",
    "PublicFrameworkTableRepairSqlTests.cs",
    "RetentionAdoptionSafetyTests.cs",
    "RowRetentionDeclarationToEnforcementTests.cs",
    "SelectRedeliveryEventsTests.cs",
    "StreamDigestTests.cs",
    "StreamsWithPendingMessagesSqlTests.cs",
  ];

  /// <summary>A <c>[NotInParallel]</c> attribute, keyed or not, starting its line.</summary>
  [GeneratedRegex(@"^\s*\[NotInParallel\b[^\n]*$", RegexOptions.Multiline)]
  private static partial Regex _serializes();

  /// <summary>A line comment after the attribute with at least three words in it: the reason.</summary>
  [GeneratedRegex(@"\]\s*//\s*\S+\s+\S+\s+\S+")]
  private static partial Regex _saysWhy();

  [GeneratedRegex(@"\[assembly:\s*ParallelLimiter<EFCorePostgresParallelism>\]")]
  private static partial Regex _limitsTheAssembly();

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

  /// <summary>Every source file of the project, by its path relative to the project, with '/' separators.</summary>
  private static List<(string Path, string Text)> _sources() {
    var projectDirectory = _projectDirectory();
    var separator = Path.DirectorySeparatorChar;
    return [.. Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
      .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
        && !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
      .Select(path => (Path.GetRelativePath(projectDirectory, path).Replace(separator, '/'), File.ReadAllText(path)))
      .OrderBy(source => source.Item1, StringComparer.Ordinal)];
  }

  [Test]
  public async Task TheBaseClass_LeavesItsTestsParallelAsync() {
    var baseClass = _sources().Single(source => source.Path == "EFCoreTestBase.cs");

    await Assert.That(_serializes().IsMatch(baseClass.Text)).IsFalse()
      .Because("a key on the base class serializes every database test; a test that shares something says so itself");
  }

  [Test]
  public async Task NoTest_HoldsTheRetiredBlanketKeyAsync() {
    var holders = _sources()
      .Where(source => _serializes().Matches(source.Text).Any(match => match.Value.Contains($"\"{RETIRED_KEY}\"", StringComparison.Ordinal)))
      .Select(source => source.Path)
      .ToList();

    await Assert.That(holders).IsEmpty()
      .Because($"the {RETIRED_KEY} key serialized tests that share nothing; name the resource a test shares instead: "
        + string.Join(", ", holders));
  }

  [Test]
  public async Task EverySerialization_SaysWhyOnItsLineAsync() {
    var sources = _sources();
    var serializing = sources.Where(source => _serializes().IsMatch(source.Text)).ToList();

    await Assert.That(serializing.Select(source => source.Path)).Contains("LocalEventStorageTests.cs")
      .Because("the scan must find a class that serializes, or an empty answer proves nothing");

    var unexplained = serializing
      .Where(source => !_predateTheReasonRule.Contains(source.Path, StringComparer.Ordinal))
      .SelectMany(source => _serializes().Matches(source.Text)
        .Where(match => !_saysWhy().IsMatch(match.Value))
        .Select(match => $"{source.Path}: {match.Value.Trim()}"))
      .ToList();

    await Assert.That(unexplained).IsEmpty()
      .Because("a test that does not run in parallel says what it shares, as a comment on the attribute's line: "
        + string.Join(" | ", unexplained));
  }

  [Test]
  public async Task TheListOfSerializationsWithoutAReason_OnlyShrinksAsync() {
    var sources = _sources();

    var explainedOrGone = _predateTheReasonRule
      .Where(path => sources.SingleOrDefault(source => source.Path == path) is not { Text: { } text }
        || _serializes().Matches(text).All(match => _saysWhy().IsMatch(match.Value)))
      .ToList();

    await Assert.That(explainedOrGone).IsEmpty()
      .Because($"these now give a reason or no longer serialize, so take them off the list: {string.Join(", ", explainedOrGone)}");
  }

  [Test]
  public async Task TheProject_RunsUnderTheContainerSizedLimitAsync() {
    var assemblyInfo = _sources().Single(source => source.Path == "AssemblyInfo.cs");

    await Assert.That(_limitsTheAssembly().IsMatch(assemblyInfo.Text)).IsTrue()
      .Because("without the limit TUnit runs eight tests per processor, more than the one container has connections for");
    await Assert.That(new EFCorePostgresParallelism().Limit).IsEqualTo(EFCorePostgresParallelism.MAX_CONCURRENT_TESTS);
  }

  /// <summary>
  /// The container has the connections the limit needs: every process sharing it, each running its limit of tests at
  /// their peak, plus headroom.
  /// </summary>
  /// <remarks>
  /// The container is created once and reused across runs, so one made before the limit was sized (or by hand) is
  /// what a developer's machine actually has. This reads the server's own answer rather than the setting in source.
  /// </remarks>
  [Test]
  public async Task TheContainer_HasTheConnectionsTheLimitNeedsAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    await using var connection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await using var show = new NpgsqlCommand("SHOW max_connections", connection);
    var maxConnections = int.Parse((string)(await show.ExecuteScalarAsync(cancellationToken))!, CultureInfo.InvariantCulture);

    var needed = (EFCorePostgresParallelism.MAX_CONCURRENT_TESTS * CONNECTIONS_PER_TEST * PROCESSES_PER_CONTAINER)
      + HEADROOM_CONNECTIONS;
    await Assert.That(maxConnections).IsGreaterThanOrEqualTo(needed)
      .Because($"{PROCESSES_PER_CONTAINER} processes x {EFCorePostgresParallelism.MAX_CONCURRENT_TESTS} tests x "
        + $"{CONNECTIONS_PER_TEST} connections + {HEADROOM_CONNECTIONS} headroom must fit max_connections; recreate "
        + "the container (docker rm -f whizbang-test-postgres) or lower the limit");
  }
}
