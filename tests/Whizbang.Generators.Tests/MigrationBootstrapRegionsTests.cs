using System.Reflection;
using TUnit.Assertions.Extensions;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That the marked bootstrap subset is exactly what it claims to be.
/// </summary>
/// <remarks>
/// <para>
/// The subset that has to exist before a migrator can be elected is marked in the SQL rather than
/// listed in code, so the marks themselves are the specification and a mistyped one is a real
/// defect. The integration cover proves the subset is sufficient against an empty database; these
/// prove the extraction does what the marks say, including that it does not quietly take more than
/// it was given.
/// </para>
/// <para>
/// The "not more" half matters as much as the "enough" half. The eviction migration is in the
/// subset only for its tombstone table; the two functions below that table need objects the
/// bootstrap deliberately does not create, and pulling them in would make the bootstrap fail on
/// every empty database.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators.Shared/Models/MigrationBootstrapRegions.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
public class MigrationBootstrapRegionsTests {

  /// <summary>
  /// The migrations that carry a bootstrap region, and why each one has to.
  /// </summary>
  /// <remarks>
  /// Pinned as a list because marking another migration is a real decision with a real cost: every
  /// instance applies the subset on every start, before anything is elected.
  /// </remarks>
  private static readonly string[] _electionClosure = [
    "000_MigrationTracking.sql",          // the ledger tables, and drop_all_overloads, which 010 calls
    "010_RegisterInstanceHeartbeat.sql",  // how an instance joins the registry
    "106_InstanceEvictionFencing.sql",    // the tombstone table record_capability consults
    "108_InstanceCapabilities.sql",       // record_capability itself
  ];

  /// <summary>The objects an election cannot happen without.</summary>
  private static readonly string[] _requiredObjects = [
    "wh_instance_capabilities",
    "wh_instance_evictions",
    "record_capability",
    "release_capability",
    "register_instance_heartbeat",
    "drop_all_overloads",
  ];

  /// <summary>Every shipped migration, as the generator reads them.</summary>
  private static IEnumerable<(string Name, string Sql)> _migrations() {
    var assembly = System.Reflection.Assembly.Load("Whizbang.Data.EFCore.Postgres.Generators");
    const string PREFIX = "Whizbang.Data.EFCore.Postgres.Generators.Templates.Migrations.";

    foreach (var resource in assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(PREFIX, StringComparison.Ordinal)
                 && n.EndsWith(".sql", StringComparison.Ordinal))
        .OrderBy(n => n, StringComparer.Ordinal)) {
      using var stream = assembly.GetManifestResourceStream(resource)!;
      using var reader = new StreamReader(stream);
      yield return (resource[PREFIX.Length..], reader.ReadToEnd());
    }
  }

  /// <summary>Only whole lines count as markers.</summary>
  [Test]
  public async Task AFileWithNoMarkerYieldsNothingAsync() {
    await Assert.That(MigrationBootstrapRegions.Extract("CREATE TABLE t (x int);")).IsNull();
    await Assert.That(MigrationBootstrapRegions.Extract(string.Empty)).IsNull();
    await Assert.That(MigrationBootstrapRegions.Extract(null!)).IsNull();
  }

  /// <summary>A marked region is taken and the rest is left.</summary>
  [Test]
  public async Task OnlyTheMarkedRegionIsTakenAsync() {
    var extracted = MigrationBootstrapRegions.Extract($"""
      CREATE TABLE before_it (x int);
      {MigrationBootstrapRegions.BEGIN}
      CREATE TABLE inside_it (x int);
      {MigrationBootstrapRegions.END}
      CREATE TABLE after_it (x int);
      """);

    await Assert.That(extracted).IsNotNull();
    await Assert.That(extracted).Contains("inside_it", StringComparison.Ordinal);
    await Assert.That(extracted).DoesNotContain("before_it", StringComparison.Ordinal);
    await Assert.That(extracted).DoesNotContain("after_it", StringComparison.Ordinal);
  }

  /// <summary>Several regions in one file are joined in file order.</summary>
  [Test]
  public async Task SeveralRegionsAreJoinedInOrderAsync() {
    var extracted = MigrationBootstrapRegions.Extract($"""
      {MigrationBootstrapRegions.BEGIN}
      SELECT 'first';
      {MigrationBootstrapRegions.END}
      SELECT 'skipped';
      {MigrationBootstrapRegions.BEGIN}
      SELECT 'second';
      {MigrationBootstrapRegions.END}
      """)!;

    await Assert.That(extracted.IndexOf("first", StringComparison.Ordinal))
      .IsLessThan(extracted.IndexOf("second", StringComparison.Ordinal));
    await Assert.That(extracted).DoesNotContain("skipped", StringComparison.Ordinal);
  }

  /// <summary>
  /// An unclosed region runs to the end of the file rather than being dropped.
  /// </summary>
  /// <remarks>
  /// The direction a mistake should fail in. Too much bootstrap fails the first time it runs, on
  /// every empty database, and is obvious. Too little would silently leave the election cycle in
  /// place and the only symptom would be a fleet quietly duplicating work.
  /// </remarks>
  [Test]
  public async Task AnUnclosedRegionRunsToTheEndOfTheFileAsync() {
    var extracted = MigrationBootstrapRegions.Extract($"""
      {MigrationBootstrapRegions.BEGIN}
      SELECT 'kept';
      SELECT 'also kept';
      """)!;

    await Assert.That(extracted).Contains("also kept", StringComparison.Ordinal);
    await Assert.That(MigrationBootstrapRegions.MarkersAreBalanced($"""
      {MigrationBootstrapRegions.BEGIN}
      SELECT 'kept';
      """)).IsFalse();
  }

  /// <summary>Malformed markers are reported as malformed.</summary>
  [Test]
  public async Task MalformedMarkersAreDetectedAsync() {
    await Assert.That(MigrationBootstrapRegions.MarkersAreBalanced(
      $"{MigrationBootstrapRegions.END}\nSELECT 1;")).IsFalse()
      .Because("an end with no begin is a typo, not an empty region");
    await Assert.That(MigrationBootstrapRegions.MarkersAreBalanced(
      $"{MigrationBootstrapRegions.BEGIN}\n{MigrationBootstrapRegions.BEGIN}\n"
      + $"{MigrationBootstrapRegions.END}\n{MigrationBootstrapRegions.END}")).IsFalse()
      .Because("nesting has no meaning here and almost certainly means a marker was duplicated");
    await Assert.That(MigrationBootstrapRegions.MarkersAreBalanced(
      $"{MigrationBootstrapRegions.BEGIN}\nSELECT 1;\n{MigrationBootstrapRegions.END}")).IsTrue();
    await Assert.That(MigrationBootstrapRegions.MarkersAreBalanced(null!)).IsTrue();
  }

  /// <summary>
  /// Every shipped migration's markers balance.
  /// </summary>
  /// <remarks>
  /// The guard that makes the marks trustworthy. Asserted over the whole set rather than a sample,
  /// because this is the only thing standing between a typo and a bootstrap that is silently the
  /// wrong size.
  /// </remarks>
  [Test]
  public async Task EveryShippedMigrationHasBalancedMarkersAsync() {
    var offenders = _migrations()
      .Where(m => !MigrationBootstrapRegions.MarkersAreBalanced(m.Sql))
      .Select(m => m.Name)
      .ToList();

    await Assert.That(offenders).IsEmpty();
  }

  /// <summary>
  /// The bootstrap subset is the four migrations the election cycle needs, and no others.
  /// </summary>
  /// <remarks>
  /// Pinned deliberately. Marking another migration is a real decision with a real cost — every
  /// instance applies the subset on every start, before anything is elected — so it should take a
  /// deliberate change to this list rather than happening by accident.
  /// </remarks>
  [Test]
  public async Task TheBootstrapSubsetIsTheFourMigrationsTheCycleNeedsAsync() {
    var marked = _migrations()
      .Where(m => MigrationBootstrapRegions.Extract(m.Sql) is not null)
      .Select(m => m.Name)
      .OrderBy(n => n, StringComparer.Ordinal)
      .ToList();

    await Assert.That(marked).IsEquivalentTo(_electionClosure);
  }

  /// <summary>
  /// The eviction migration contributes its table and not its functions.
  /// </summary>
  /// <remarks>
  /// The reason bootstrap is a region rather than a file. That migration redefines
  /// <c>cleanup_stale_instances</c> and <c>record_heartbeat</c>, neither of which is called before
  /// the schema is migrated, and both of which reach objects the bootstrap does not create.
  /// </remarks>
  [Test]
  public async Task TheEvictionMigrationContributesItsTableAndNotItsFunctionsAsync() {
    var sql = _migrations().Single(m => m.Name == "106_InstanceEvictionFencing.sql").Sql;
    var extracted = MigrationBootstrapRegions.Extract(sql)!;

    await Assert.That(extracted).Contains(
      "CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_instance_evictions", StringComparison.Ordinal)
      .Because("record_capability reads this table on every acquisition");

    // The region's own COMMENT ON TABLE mentions both functions by name, so what has to be absent
    // is their definitions rather than the words.
    await Assert.That(extracted).DoesNotContain(
      "FUNCTION __SCHEMA__.cleanup_stale_instances", StringComparison.Ordinal);
    await Assert.That(extracted).DoesNotContain(
      "FUNCTION __SCHEMA__.record_heartbeat", StringComparison.Ordinal);
  }

  /// <summary>
  /// The subset carries the objects an election needs.
  /// </summary>
  /// <remarks>
  /// A static counterpart to the integration test, so a migration edit that breaks the closure is
  /// caught by a fast test rather than only by one that needs a database. It checks that the
  /// statements are present, not that they work; only the integration test can say that.
  /// </remarks>
  [Test]
  public async Task TheSubsetCarriesWhatAnElectionNeedsAsync() {
    var bootstrap = string.Join("\n", _migrations()
      .Select(m => MigrationBootstrapRegions.Extract(m.Sql))
      .Where(s => s is not null));

    foreach (var required in _requiredObjects) {
      await Assert.That(bootstrap).Contains(required, StringComparison.Ordinal)
        .Because($"an election cannot happen without {required}");
    }
  }
}
