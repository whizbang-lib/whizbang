// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Configuration;
using Whizbang.Core.Startup;
using Whizbang.Core.Tests.Capabilities;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <c>Whizbang:Workers:Maintenance:TableRewriteStrategy</c> resolved against <c>AllowTableRewrite</c> and whether
/// pg_squeeze is usable: one test per cell of the resolution matrix (four values, pg_squeeze usable or not,
/// AllowTableRewrite or not). <c>Auto</c> never starts an online rewrite (decision 1, #1671): it rewrites with
/// <c>VACUUM FULL</c> when permitted and otherwise only detects and reports. An explicit <c>PgSqueeze</c> is never
/// replaced by a blocking rewrite, and it requires pg_squeeze, so on a server that does not allow it start fails
/// with the fix.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#table-rewrite-strategy</docs>
public class TableRewriteStrategyTests {
  private const string ONLINE_HINT = "Auto never starts an online rewrite";
  private const string NOT_PERMITTED = "VACUUM FULL is not permitted (Whizbang:Workers:Maintenance:AllowTableRewrite = false)";

  [Test]
  // Auto: VACUUM FULL when permitted, otherwise detect and report; a usable pg_squeeze is only mentioned.
  [Arguments(TableRewriteStrategy.Auto, true, true, TableRewriteStrategies.VACUUM_FULL, ONLINE_HINT)]
  [Arguments(TableRewriteStrategy.Auto, true, false, null, ONLINE_HINT)]
  [Arguments(TableRewriteStrategy.Auto, false, true, TableRewriteStrategies.VACUUM_FULL, "Auto: VACUUM FULL at boot")]
  [Arguments(TableRewriteStrategy.Auto, false, false, null, NOT_PERMITTED)]
  // PgSqueeze: the explicit online choice, whatever AllowTableRewrite says, and never VACUUM FULL in its place.
  [Arguments(TableRewriteStrategy.PgSqueeze, true, true, TableRewriteStrategies.PG_SQUEEZE, "PgSqueeze: online rewrite through pg_squeeze")]
  [Arguments(TableRewriteStrategy.PgSqueeze, true, false, TableRewriteStrategies.PG_SQUEEZE, "PgSqueeze: online rewrite through pg_squeeze")]
  [Arguments(TableRewriteStrategy.PgSqueeze, false, true, TableRewriteStrategies.PG_SQUEEZE, "pg_squeeze is not usable here")]
  [Arguments(TableRewriteStrategy.PgSqueeze, false, false, TableRewriteStrategies.PG_SQUEEZE, "pg_squeeze is not usable here")]
  // VacuumFull: only when permitted; pg_squeeze plays no part.
  [Arguments(TableRewriteStrategy.VacuumFull, true, true, TableRewriteStrategies.VACUUM_FULL, "VacuumFull: VACUUM FULL at boot")]
  [Arguments(TableRewriteStrategy.VacuumFull, true, false, null, NOT_PERMITTED)]
  [Arguments(TableRewriteStrategy.VacuumFull, false, true, TableRewriteStrategies.VACUUM_FULL, "VacuumFull: VACUUM FULL at boot")]
  [Arguments(TableRewriteStrategy.VacuumFull, false, false, null, NOT_PERMITTED)]
  // None: nothing is rewritten, whatever else holds.
  [Arguments(TableRewriteStrategy.None, true, true, null, "None: no table is rewritten")]
  [Arguments(TableRewriteStrategy.None, true, false, null, "None: no table is rewritten")]
  [Arguments(TableRewriteStrategy.None, false, true, null, "None: no table is rewritten")]
  [Arguments(TableRewriteStrategy.None, false, false, null, "None: no table is rewritten")]
  public async Task Resolve_EachCellOfTheMatrixAsync(
      TableRewriteStrategy strategy, bool pgSqueezeUsable, bool allowTableRewrite, string? rewriter, string reason) {
    var plan = TableRewriteStrategyResolver.Resolve(strategy, allowTableRewrite, pgSqueezeUsable);

    await Assert.That(plan.Strategy).IsEqualTo(rewriter);
    await Assert.That(plan.Reason).Contains(reason);
  }

  [Test]
  public async Task Auto_WithPgSqueezeUsable_SaysHowToOptIn_AndNeverPicksItAsync() {
    var plan = TableRewriteStrategyResolver.Resolve(TableRewriteStrategy.Auto, allowTableRewrite: false, pgSqueezeUsable: true);

    await Assert.That(plan.Reason).IsEqualTo(
      "Auto detects and reports only: " + NOT_PERMITTED + "; pg_squeeze is usable, but Auto never starts an online rewrite: "
      + "set Whizbang:Workers:Maintenance:TableRewriteStrategy = PgSqueeze to opt in");
  }

  [Test]
  public async Task Resolve_AValueOutsideTheEnum_IsRefusedAsync() {
    await Assert.That(() => TableRewriteStrategyResolver.Resolve((TableRewriteStrategy)42, true, true))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task TheSetting_DefaultsToAuto_AndBindsFromConfigurationAsync() {
    await Assert.That(new MaintenanceWorkerOptions().TableRewriteStrategy).IsEqualTo(TableRewriteStrategy.Auto);
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Workers:Maintenance:TableRewriteStrategy"] = "PgSqueeze",
    }).Build());
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.TableRewriteStrategy)
      .IsEqualTo(TableRewriteStrategy.PgSqueeze);
  }

  [Test]
  public async Task PgSqueeze_IsTheLibrarysFirstSettingDrivenRequirementAsync() {
    var requirement = ConfigurationManifestRegistry.Requirements
      .Single(r => r.Key == "Whizbang:Workers:Maintenance:TableRewriteStrategy");

    await Assert.That(requirement.Capability).IsEqualTo(new CapabilityId("postgres", "pg_squeeze"));
    await Assert.That(requirement.Feature).IsEqualTo("Online table rewrite");
    await Assert.That(requirement.Value).IsEqualTo("PgSqueeze");
    await Assert.That(requirement.Default).IsEqualTo("Auto")
      .Because("Auto does not use pg_squeeze, so the default never puts the requirement in force");
  }

  private static async Task<CapabilityVerdict> _verdictAsync(
      PostgresExtensionState squeeze, IReadOnlyDictionary<string, string?> settings) {
    var descriptor = PostgresExtensionCatalog.Describe(PostgresExtensionCatalog.BuiltIn.Single(e => e.Name == "pg_squeeze"));
    var provider = new FakeCapabilityProvider("postgres", ["orders"], descriptor);
    provider.States["pg_squeeze"] = PostgresCapabilityProbe.ToCapabilityState(squeeze);
    var registry = new CapabilityRegistry([provider], NullLogger<CapabilityRegistry>.Instance);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var step = new CapabilitiesStartupStep(registry, Options.Create(new CapabilityOptions()),
      [new ConfigurationManifestRequirementSource()], [], configuration, NullLogger<CapabilitiesStartupStep>.Instance);
    await step.ExecuteAsync(CancellationToken.None);
    return registry.Verdict;
  }

  private static readonly Dictionary<string, string?> _pgSqueeze = new() {
    ["Whizbang:Workers:Maintenance:TableRewriteStrategy"] = "PgSqueeze",
  };

  [Test]
  public async Task ExplicitPgSqueeze_OnAServerThatDoesNotAllowlistIt_FailsStartWithTheFixAsync() {
    var verdict = await _verdictAsync(PostgresExtensionState.NotAllowlisted, _pgSqueeze);

    var failure = verdict.Failures.Single();
    await Assert.That(failure.Line).IsEqualTo(
      "capability postgres/pg_squeeze on 'orders': NotPermitted, 1.0. Fix: add PG_SQUEEZE to the server's extension "
      + "allowlist (azure.extensions on Azure Database for PostgreSQL) and pg_squeeze to shared_preload_libraries, then "
      + "restart the server. Required by: Online table rewrite (Whizbang:Workers:Maintenance:TableRewriteStrategy = PgSqueeze, explicit)");
  }

  [Test]
  public async Task ExplicitPgSqueeze_OnAServerWithoutThePreload_FailsStartWithThePreloadFixAsync() {
    var verdict = await _verdictAsync(PostgresExtensionState.NeedsPreload, _pgSqueeze);

    await Assert.That(verdict.Failures.Single().Line)
      .Contains("Fix: add pg_squeeze to shared_preload_libraries, then restart the server.");
  }

  [Test]
  public async Task Auto_PutsNoRequirementInForce_WhateverTheServerSaysAsync() {
    var verdict = await _verdictAsync(PostgresExtensionState.NotAllowlisted, new Dictionary<string, string?>());
    var autoSet = await _verdictAsync(PostgresExtensionState.NotAllowlisted, new Dictionary<string, string?> {
      ["Whizbang:Workers:Maintenance:TableRewriteStrategy"] = "Auto",
    });

    await Assert.That(verdict.Entries).IsEmpty();
    await Assert.That(autoSet.Entries).IsEmpty();
  }
}
