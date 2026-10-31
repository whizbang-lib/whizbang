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
using Whizbang.Core.Tests.Capabilities;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <c>Whizbang:Workers:Maintenance:Partitioning:Strategy</c> (#1752): who creates the partitions of the framework's
/// partitioned tables. <c>Auto</c> hands creation to pg_partman only where pg_partman is Ready, and otherwise keeps the
/// built-in manager and says why; it never installs pg_partman. An explicit <c>PgPartman</c> requires pg_partman, so a
/// server that cannot provide it fails start with the fix. Whichever creates them, the built-in manager drops expired
/// partitions, so the signal carry-forward (#1758) runs before every drop.
/// </summary>
/// <docs>operations/infrastructure/partitioned-ledgers#pg-partman</docs>
/// <code-under-test>src/Whizbang.Core/Workers/PartitionManagementStrategy.cs</code-under-test>
public class PartitionManagementStrategyTests {
  private const string KEY = "Whizbang:Workers:Maintenance:Partitioning:Strategy";
  private const string BUILT_IN_DOES_IT = "so the built-in manager creates and drops partitions";
  private const string PG_PARTMAN_CREATES = "pg_partman creates partitions ahead; the built-in manager still drops expired ones";

  [Test]
  // Auto: pg_partman only when Ready; any other state keeps the built-in manager, and the reason names the state.
  [Arguments(PartitionManagementStrategy.Auto, CapabilityState.Ready, true, "Auto: pg_partman is Ready, so " + PG_PARTMAN_CREATES)]
  [Arguments(PartitionManagementStrategy.Auto, CapabilityState.Provisionable, false, "Auto never installs it")]
  [Arguments(PartitionManagementStrategy.Auto, CapabilityState.NotPermitted, false, "Auto: pg_partman is NotPermitted, " + BUILT_IN_DOES_IT)]
  [Arguments(PartitionManagementStrategy.Auto, CapabilityState.NeedsServerChange, false, "Auto: pg_partman is NeedsServerChange, " + BUILT_IN_DOES_IT)]
  [Arguments(PartitionManagementStrategy.Auto, CapabilityState.NoPrivilege, false, "Auto: pg_partman is NoPrivilege, " + BUILT_IN_DOES_IT)]
  [Arguments(PartitionManagementStrategy.Auto, CapabilityState.Missing, false, "Auto: pg_partman is Missing, " + BUILT_IN_DOES_IT)]
  [Arguments(PartitionManagementStrategy.Auto, CapabilityState.Unknown, false, "Auto: pg_partman is Unknown, " + BUILT_IN_DOES_IT)]
  // BuiltIn: never pg_partman, even where it is Ready.
  [Arguments(PartitionManagementStrategy.BuiltIn, CapabilityState.Ready, false, "BuiltIn: the built-in manager creates and drops partitions")]
  [Arguments(PartitionManagementStrategy.BuiltIn, CapabilityState.Missing, false, "BuiltIn: the built-in manager creates and drops partitions")]
  // PgPartman: always pg_partman; the capability verdict, not the resolver, stops start where it cannot be had.
  [Arguments(PartitionManagementStrategy.PgPartman, CapabilityState.Ready, true, "PgPartman: " + PG_PARTMAN_CREATES)]
  [Arguments(PartitionManagementStrategy.PgPartman, CapabilityState.Unknown, true, "pg_partman reads Unknown")]
  public async Task Resolve_EachCellAsync(PartitionManagementStrategy strategy, CapabilityState pgPartman, bool usePgPartman, string reason) {
    var plan = PartitionManagementStrategyResolver.Resolve(strategy, pgPartman);

    await Assert.That(plan.UsePgPartman).IsEqualTo(usePgPartman);
    await Assert.That(plan.Reason).Contains(reason);
  }

  [Test]
  public async Task Auto_WhenPgPartmanIsOnlyAvailable_SaysHowToOptInAsync() {
    var plan = PartitionManagementStrategyResolver.Resolve(PartitionManagementStrategy.Auto, CapabilityState.Provisionable);

    await Assert.That(plan.Reason).IsEqualTo(
      "Auto: pg_partman is available but not installed, and Auto never installs it, " + BUILT_IN_DOES_IT
      + "; set " + KEY + " = PgPartman to install and use it");
  }

  [Test]
  public async Task OnlyAuto_FallsBackToTheBuiltInManager_WhenAPgPartmanPassFailsAsync() {
    await Assert.That(PartitionManagementStrategyResolver.Resolve(PartitionManagementStrategy.Auto, CapabilityState.Ready).BuiltInOnFailure)
      .IsTrue().Because("Auto chose pg_partman itself, so a pass that fails is reported and the cycle continues built in");
    await Assert.That(PartitionManagementStrategyResolver.Resolve(PartitionManagementStrategy.PgPartman, CapabilityState.Ready).BuiltInOnFailure)
      .IsFalse().Because("an explicit choice fails loudly");
  }

  [Test]
  public async Task Resolve_AValueOutsideTheEnum_IsRefusedAsync() {
    await Assert.That(() => PartitionManagementStrategyResolver.Resolve((PartitionManagementStrategy)42, CapabilityState.Ready))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task PgPartmanState_IsReady_OnlyWhenEveryProbedDatabaseSaysSoAsync() {
    await Assert.That(PartitionManagementStrategyResolver.PgPartmanState(null)).IsEqualTo(CapabilityState.Unknown)
      .Because("with no capability registry nothing has probed pg_partman");
    await Assert.That(PartitionManagementStrategyResolver.PgPartmanState(await _registryAsync())).IsEqualTo(CapabilityState.Unknown)
      .Because("a registry with no Postgres provider holds no status for pg_partman");
    await Assert.That(PartitionManagementStrategyResolver.PgPartmanState(await _registryAsync(CapabilityState.Ready, CapabilityState.Ready)))
      .IsEqualTo(CapabilityState.Ready);
    await Assert.That(PartitionManagementStrategyResolver.PgPartmanState(await _registryAsync(CapabilityState.Ready, CapabilityState.NotPermitted)))
      .IsEqualTo(CapabilityState.NotPermitted).Because("the first database where it is not Ready decides");
  }

  private static async Task<CapabilityRegistry> _registryAsync(params CapabilityState[] states) {
    var providers = new List<ICapabilityProvider>();
    var descriptor = PostgresExtensionCatalog.Describe(PostgresExtensionCatalog.BuiltIn.Single(e => e.Name == "pg_partman"));
    for (var i = 0; i < states.Length; i++) {
      var provider = new FakeCapabilityProvider("postgres", [$"db{i}"], descriptor);
      provider.States["pg_partman"] = states[i];
      providers.Add(provider);
    }
    var registry = new CapabilityRegistry(providers, NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);
    return registry;
  }

  [Test]
  public async Task TheSetting_DefaultsToAuto_AndBindsFromConfigurationAsync() {
    await Assert.That(new MaintenancePartitioningOptions().Strategy).IsEqualTo(PartitionManagementStrategy.Auto);
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      [KEY] = "PgPartman",
    }).Build());
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.Partitioning.Strategy)
      .IsEqualTo(PartitionManagementStrategy.PgPartman);
  }

  /// <summary>
  /// The requirements Whizbang.Core's own generated manifest declares. The registry is process-wide and other tests
  /// register manifests of their own, so these read the library's manifest alone.
  /// </summary>
  private static IReadOnlyList<CapabilityRequirement> _libraryRequirements() =>
    [.. ConfigurationManifestRegistry.Manifests
      .Single(manifest => manifest.Assembly == typeof(MaintenanceWorkerOptions).Assembly.GetName().Name)
      .Bindings.SelectMany(binding => binding.Requirements)];

  private sealed class LibraryRequirements : ICapabilityRequirementSource {
    public IReadOnlyList<ActiveCapabilityRequirement> Resolve(IConfiguration? configuration) =>
      CapabilityActivationResolver.Resolve(_libraryRequirements(), configuration);
  }

  [Test]
  public async Task PgPartman_DeclaresPgPartman_AndTheDefaultDoesNotAsync() {
    var requirement = _libraryRequirements().Single(r => r.Key == KEY);

    await Assert.That(requirement.Capability).IsEqualTo(new CapabilityId("postgres", "pg_partman"));
    await Assert.That(requirement.Feature).IsEqualTo("Partition management");
    await Assert.That(requirement.Value).IsEqualTo("PgPartman");
    await Assert.That(requirement.Default).IsEqualTo("Auto")
      .Because("Auto only uses a pg_partman that is already Ready, so the default never requires or provisions it");
  }

  private static async Task<CapabilityVerdict> _verdictAsync(PostgresExtensionState partman, IReadOnlyDictionary<string, string?> settings) {
    var descriptor = PostgresExtensionCatalog.Describe(PostgresExtensionCatalog.BuiltIn.Single(e => e.Name == "pg_partman"));
    var provider = new FakeCapabilityProvider("postgres", ["orders"], descriptor);
    provider.States["pg_partman"] = PostgresCapabilityProbe.ToCapabilityState(partman);
    var registry = new CapabilityRegistry([provider], NullLogger<CapabilityRegistry>.Instance);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var options = new CapabilityOptions { AutoProvision = false };
    var step = new CapabilitiesStartupStep(registry, Options.Create(options),
      [new LibraryRequirements()], [], configuration, NullLogger<CapabilitiesStartupStep>.Instance);
    await step.ExecuteAsync(CancellationToken.None);
    return registry.Verdict;
  }

  private static readonly Dictionary<string, string?> _pgPartman = new() { [KEY] = "PgPartman" };

  [Test]
  public async Task ExplicitPgPartman_OnAServerThatDoesNotAllowlistIt_FailsStartWithTheFixAsync() {
    var verdict = await _verdictAsync(PostgresExtensionState.NotAllowlisted, _pgPartman);

    await Assert.That(verdict.Failures.Single().Line).IsEqualTo(
      "capability postgres/pg_partman on 'orders': NotPermitted, 1.0. Fix: add PG_PARTMAN to the server's extension "
      + "allowlist (azure.extensions on Azure Database for PostgreSQL). "
      + "Required by: Partition management (" + KEY + " = PgPartman, explicit)");
  }

  [Test]
  public async Task ExplicitPgPartman_WhereTheServerHasNoPgPartman_FailsStartAsync() {
    var verdict = await _verdictAsync(PostgresExtensionState.Missing, _pgPartman);

    await Assert.That(verdict.Failures.Single().Line).StartsWith("capability postgres/pg_partman on 'orders': Missing");
  }

  [Test]
  public async Task ExplicitPgPartman_WhereItIsReady_ProceedsAsync() {
    var verdict = await _verdictAsync(PostgresExtensionState.Installed, _pgPartman);

    await Assert.That(verdict.Failures).IsEmpty();
    await Assert.That(verdict.Entries.Single().Outcome).IsEqualTo(CapabilityVerdictOutcome.Proceed);
  }

  [Test]
  public async Task AutoAndBuiltIn_PutNoRequirementInForce_WhateverTheServerSaysAsync() {
    var unset = await _verdictAsync(PostgresExtensionState.NotAllowlisted, new Dictionary<string, string?>());
    var auto = await _verdictAsync(PostgresExtensionState.NotAllowlisted, new Dictionary<string, string?> { [KEY] = "Auto" });
    var builtIn = await _verdictAsync(PostgresExtensionState.Missing, new Dictionary<string, string?> { [KEY] = "BuiltIn" });

    await Assert.That(unset.Entries).IsEmpty();
    await Assert.That(auto.Entries).IsEmpty();
    await Assert.That(builtIn.Entries).IsEmpty();
  }
}
