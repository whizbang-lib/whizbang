// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.PostgresExtensions;

/// <summary>
/// The state an extension is given on one database, derived from what the probe reads, and the parsing
/// of the two server settings the derivation reads (the managed-server allowlist and
/// <c>shared_preload_libraries</c>). The derivation is a pure function, so every state is pinned here
/// without a server; the integration tests prove the query feeds it what a real server says.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
public class PostgresCapabilityProbeTests {
  private static readonly PostgresExtensionInfo _plain = new(
    "pg_trgm", "substring (trigram) indexes", ["Substring indexes"], LibraryMayCreate: true,
    PostgresExtensionPrerequisites.Allowlist);

  private static readonly PostgresExtensionInfo _preloaded = new(
    "pg_stat_statements", "query statistics", ["Expensive-predicate advice"], LibraryMayCreate: true,
    PostgresExtensionPrerequisites.Allowlist | PostgresExtensionPrerequisites.SharedPreloadLibraries);

  /// <summary>A superuser on a server with no allowlist and the extension's files present, not installed.</summary>
  private static PostgresExtensionProbeRow _row(string name) => new(
    Name: name,
    InstalledVersion: null,
    ControlFilesPresent: true,
    Trusted: false,
    Allowlist: null,
    SharedPreloadLibraries: "",
    CanCreateInDatabase: true,
    IsSuperuser: true);

  // ---- one test per state ----

  [Test]
  public async Task Installed_WhenPresentInTheDatabaseAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain, _row("pg_trgm") with { InstalledVersion = "1.6" });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Installed);
  }

  [Test]
  public async Task Installed_WhenPresentAndItsLibraryIsPreloadedAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_preloaded,
      _row("pg_stat_statements") with { InstalledVersion = "1.11", SharedPreloadLibraries = "pg_stat_statements" });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Installed);
  }

  [Test]
  public async Task Available_WhenCreatableAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain, _row("pg_trgm"));

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Available);
  }

  [Test]
  public async Task NotAllowlisted_WhenAnAllowlistExistsAndDoesNotNameItAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain, _row("pg_trgm") with { Allowlist = "VECTOR,PG_CRON" });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.NotAllowlisted);
  }

  [Test]
  public async Task NeedsPreload_WhenInstalledButItsLibraryIsNotPreloadedAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_preloaded, _row("pg_stat_statements") with { InstalledVersion = "1.11" });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.NeedsPreload)
      .Because("installed but unloaded, every read of its view fails");
  }

  [Test]
  public async Task NeedsPreload_WhenAllowlistedButItsLibraryIsNotPreloadedAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_preloaded,
      _row("pg_stat_statements") with { Allowlist = "PG_STAT_STATEMENTS", SharedPreloadLibraries = "pg_cron" });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.NeedsPreload);
  }

  [Test]
  public async Task NoPrivilege_WhenTheRoleLacksCreateOnTheDatabaseAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain,
      _row("pg_trgm") with { IsSuperuser = false, Trusted = true, CanCreateInDatabase = false });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.NoPrivilege);
  }

  [Test]
  public async Task NoPrivilege_WhenUntrustedWithNoAllowlistAndNotSuperuserAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain,
      _row("pg_trgm") with { IsSuperuser = false, Trusted = false, CanCreateInDatabase = true });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.NoPrivilege)
      .Because("an untrusted extension needs a superuser unless a managed allowlist grants it");
  }

  [Test]
  public async Task Missing_WhenTheServerHasNoControlFilesAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain, _row("pg_trgm") with { ControlFilesPresent = false });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Missing);
  }

  [Test]
  public async Task Missing_TakesPrecedenceOverTheAllowlistAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain,
      _row("pg_trgm") with { ControlFilesPresent = false, Allowlist = "" });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Missing)
      .Because("allowlisting cannot help an extension the server has no files for");
  }

  // ---- privilege: the ways a non-superuser can create ----

  [Test]
  public async Task Available_ForATrustedExtensionAndARoleWithCreateAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain,
      _row("pg_trgm") with { IsSuperuser = false, Trusted = true, CanCreateInDatabase = true });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Available);
  }

  [Test]
  public async Task Available_ForAnUntrustedButAllowlistedExtensionAndARoleWithCreateAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain,
      _row("pg_trgm") with { IsSuperuser = false, Trusted = false, CanCreateInDatabase = true, Allowlist = "PG_TRGM" });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Available)
      .Because("on a managed server the allowlist is what lets the administrator role create it");
  }

  // ---- a role that cannot read shared_preload_libraries ----

  [Test]
  public async Task Unknown_WhenInstalledButThePreloadListCannotBeReadAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_preloaded,
      _row("pg_stat_statements") with { InstalledVersion = "1.11", SharedPreloadLibraries = null });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Unknown)
      .Because("without pg_read_all_settings the role cannot see whether the library is loaded");
  }

  [Test]
  public async Task Unknown_WhenCreatableButThePreloadListCannotBeReadAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_preloaded, _row("pg_stat_statements") with { SharedPreloadLibraries = null });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Unknown);
  }

  [Test]
  public async Task AnUnreadablePreloadList_DoesNotAffectAnExtensionThatNeedsNoPreloadAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain, _row("pg_trgm") with { SharedPreloadLibraries = null });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Available);
  }

  [Test]
  public async Task AnUnreadablePreloadList_DoesNotHideANotAllowlistedExtensionAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_preloaded,
      _row("pg_stat_statements") with { SharedPreloadLibraries = null, Allowlist = "VECTOR" });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.NotAllowlisted);
  }

  // ---- allowlist parsing ----

  [Test]
  public async Task Allowlist_MatchesUpperCaseNamesAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain, _row("pg_trgm") with { Allowlist = "VECTOR,PG_TRGM" });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Available);
  }

  [Test]
  public async Task Allowlist_TrimsSpacesAroundNamesAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain, _row("pg_trgm") with { Allowlist = " vector ,  pg_trgm , " });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Available);
  }

  [Test]
  public async Task Allowlist_Empty_AllowsNothingAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain, _row("pg_trgm") with { Allowlist = "" });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.NotAllowlisted)
      .Because("an allowlist that exists and names nothing is still an allowlist");
  }

  [Test]
  public async Task Allowlist_Absent_MeansNoAllowlistAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_plain, _row("pg_trgm") with { Allowlist = null });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Available);
  }

  [Test]
  public async Task ParseList_Absent_IsNullAsync() {
    await Assert.That(PostgresCapabilityProbe.ParseList(null)).IsNull();
  }

  [Test]
  public async Task ParseList_IsCaseInsensitive_TrimsSpacesAndQuotes_AndDropsEmptyEntriesAsync() {
    var list = PostgresCapabilityProbe.ParseList(" VECTOR , \"pg_cron\",, PG_TRGM ");

    await Assert.That(list).IsNotNull();
    await Assert.That(list!.Count).IsEqualTo(3);
    await Assert.That(list.Contains("vector")).IsTrue();
    await Assert.That(list.Contains("pg_cron")).IsTrue();
    await Assert.That(list.Contains("pg_trgm")).IsTrue();
  }

  [Test]
  public async Task ParseList_Empty_IsAnEmptyListNotNullAsync() {
    var list = PostgresCapabilityProbe.ParseList("");

    await Assert.That(list).IsNotNull();
    await Assert.That(list!.Count).IsEqualTo(0);
  }

  [Test]
  public async Task PreloadList_MatchesCaseInsensitivelyAsync() {
    var state = PostgresCapabilityProbe.DeriveState(_preloaded,
      _row("pg_stat_statements") with { SharedPreloadLibraries = "pg_cron, PG_STAT_STATEMENTS" });

    await Assert.That(state).IsEqualTo(PostgresExtensionState.Available);
  }

  // ---- the probe never throws for a database it cannot reach ----

  [Test]
  public async Task ProbeAsync_ConnectionFails_EveryExtensionIsUnknownAsync() {
    var target = new PostgresExtensionProbeTarget("orders-db",
      _ => throw new NpgsqlException("connection refused"));

    var result = await PostgresCapabilityProbe.ProbeAsync(target, PostgresExtensionCatalog.BuiltIn, CancellationToken.None);

    await Assert.That(result.Database).IsEqualTo("orders-db");
    await Assert.That(result.ProbeFailure).Contains("connection refused");
    await Assert.That(result.Extensions.Count).IsEqualTo(PostgresExtensionCatalog.BuiltIn.Count);
    await Assert.That(result.Extensions.All(e => e.State == PostgresExtensionState.Unknown)).IsTrue();
  }

  [Test]
  public async Task ProbeAsync_ATimeoutThatIsNotTheCallersCancellation_IsUnknownAsync() {
    var target = new PostgresExtensionProbeTarget("orders-db",
      _ => throw new OperationCanceledException("command timed out"));

    var result = await PostgresCapabilityProbe.ProbeAsync(target, PostgresExtensionCatalog.BuiltIn, CancellationToken.None);

    await Assert.That(result.ProbeFailure).Contains("command timed out");
  }

  [Test]
  public async Task ProbeAsync_TheCallersCancellation_PropagatesAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    var target = new PostgresExtensionProbeTarget("orders-db",
      ct => throw new OperationCanceledException(ct));

    await Assert.That(async () => await PostgresCapabilityProbe.ProbeAsync(target, PostgresExtensionCatalog.BuiltIn, cts.Token))
      .Throws<OperationCanceledException>()
      .Because("host shutdown is not a probe failure");
  }
}
