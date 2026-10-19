// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.PostgresExtensions;

/// <summary>
/// The catalog of extensions the library knows about: which ones, whether the library may create each,
/// what each needs from the server, and what an operator does about each state.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
public class PostgresExtensionCatalogTests {
  private static readonly string[] _known = [
    "vector", "pg_trgm", "pg_squeeze", "pg_repack", "pg_partman", "pgstattuple", "pg_stat_statements", "pg_cron",
  ];

  private static PostgresExtensionInfo _entry(string name) =>
    PostgresExtensionCatalog.BuiltIn.Single(e => e.Name == name);

  [Test]
  public async Task BuiltIn_NamesExactlyTheEightKnownExtensionsAsync() {
    var names = PostgresExtensionCatalog.BuiltIn.Select(e => e.Name).ToArray();

    await Assert.That(names).IsEquivalentTo(_known);
  }

  [Test]
  public async Task BuiltIn_EveryEntrySaysWhatItEnablesAndWhichFeaturesUseItAsync() {
    await Assert.That(PostgresExtensionCatalog.BuiltIn.Count).IsEqualTo(_known.Length)
      .Because("a loop over an empty catalog would pass without checking anything");
    foreach (var entry in PostgresExtensionCatalog.BuiltIn) {
      await Assert.That(entry.Enables).IsNotEmpty();
      await Assert.That(entry.Features.Count).IsGreaterThan(0);
      await Assert.That(entry.Prerequisites.HasFlag(PostgresExtensionPrerequisites.Allowlist)).IsTrue()
        .Because("a managed server's allowlist gates every extension, so every entry declares it");
    }
  }

  [Test]
  [Arguments("vector", true)]
  [Arguments("pg_trgm", true)]
  [Arguments("pg_squeeze", true)]
  [Arguments("pg_partman", true)]
  [Arguments("pgstattuple", true)]
  [Arguments("pg_stat_statements", true)]
  [Arguments("pg_repack", true)]
  [Arguments("pg_cron", false)]
  public async Task LibraryMayCreate_MatchesTheDesignAsync(string name, bool mayCreate) {
    await Assert.That(_entry(name).LibraryMayCreate).IsEqualTo(mayCreate);
  }

  [Test]
  [Arguments("pg_squeeze", true)]
  [Arguments("pg_stat_statements", true)]
  [Arguments("pg_cron", true)]
  [Arguments("vector", false)]
  [Arguments("pg_trgm", false)]
  [Arguments("pg_partman", false)]
  [Arguments("pgstattuple", false)]
  [Arguments("pg_repack", false)]
  public async Task NeedsPreload_FollowsThePrerequisitesAsync(string name, bool needsPreload) {
    await Assert.That(_entry(name).NeedsPreload).IsEqualTo(needsPreload);
  }

  [Test]
  public async Task Prerequisites_PgSqueezeNeedsLogicalWal_PgRepackNeedsItsClientAsync() {
    await Assert.That(_entry("pg_squeeze").Prerequisites.HasFlag(PostgresExtensionPrerequisites.LogicalWalLevel)).IsTrue();
    await Assert.That(_entry("pg_repack").Prerequisites.HasFlag(PostgresExtensionPrerequisites.ClientBinary)).IsTrue();
  }

  [Test]
  public async Task Features_DescribeLibraryManagedUse_NotDetectionOnlyAsync() {
    await Assert.That(string.Join(" ", _entry("pg_repack").Features)).Contains("in process or external job");
    await Assert.That(string.Join(" ", _entry("pg_repack").Features)).DoesNotContain("detection only");
    await Assert.That(string.Join(" ", _entry("pg_squeeze").Features)).Contains("table rewriter");
    await Assert.That(string.Join(" ", _entry("pg_partman").Features)).Contains("create_parent");
    await Assert.That(string.Join(" ", _entry("pg_partman").Features)).Contains("run_maintenance");
  }

  // ---- remedies, one per state ----

  [Test]
  [Arguments(PostgresExtensionState.Installed)]
  [Arguments(PostgresExtensionState.Available)]
  public async Task Remedy_UsableStates_NeedNothingAsync(PostgresExtensionState state) {
    await Assert.That(PostgresExtensionCatalog.Remedy(_entry("pg_trgm"), state)).IsEqualTo("none");
  }

  [Test]
  public async Task Remedy_NotAllowlisted_NamesTheUpperCaseAllowlistEntryAsync() {
    var remedy = PostgresExtensionCatalog.Remedy(_entry("pg_trgm"), PostgresExtensionState.NotAllowlisted);

    await Assert.That(remedy).Contains("PG_TRGM");
    await Assert.That(remedy).Contains("allowlist");
    await Assert.That(remedy).DoesNotContain("shared_preload_libraries");
  }

  [Test]
  public async Task Remedy_NotAllowlisted_ForAPreloadedExtension_AlsoNamesThePreloadListAsync() {
    var remedy = PostgresExtensionCatalog.Remedy(_entry("pg_squeeze"), PostgresExtensionState.NotAllowlisted);

    await Assert.That(remedy).Contains("PG_SQUEEZE");
    await Assert.That(remedy).Contains("shared_preload_libraries");
    await Assert.That(remedy).Contains("restart");
  }

  [Test]
  public async Task Remedy_NeedsPreload_NamesTheLibraryAndTheRestartAsync() {
    var remedy = PostgresExtensionCatalog.Remedy(_entry("pg_stat_statements"), PostgresExtensionState.NeedsPreload);

    await Assert.That(remedy).Contains("pg_stat_statements");
    await Assert.That(remedy).Contains("shared_preload_libraries");
    await Assert.That(remedy).Contains("restart");
  }

  [Test]
  public async Task Remedy_NoPrivilege_SaysWhoCanCreateItAsync() {
    var remedy = PostgresExtensionCatalog.Remedy(_entry("pgstattuple"), PostgresExtensionState.NoPrivilege);

    await Assert.That(remedy).Contains("CREATE EXTENSION pgstattuple");
  }

  [Test]
  public async Task Remedy_Missing_SaysToInstallTheFilesAsync() {
    var remedy = PostgresExtensionCatalog.Remedy(_entry("pg_squeeze"), PostgresExtensionState.Missing);

    await Assert.That(remedy).Contains("install");
    await Assert.That(remedy).Contains("pg_squeeze");
  }

  [Test]
  public async Task Remedy_Unknown_PointsAtTheProbeAsync() {
    var remedy = PostgresExtensionCatalog.Remedy(_entry("vector"), PostgresExtensionState.Unknown);

    await Assert.That(remedy).Contains("probe");
  }
}
