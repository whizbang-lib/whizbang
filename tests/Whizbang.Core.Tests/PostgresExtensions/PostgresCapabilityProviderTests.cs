// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.PostgresExtensions;

/// <summary>
/// Postgres extensions as the first capability provider: each Postgres state maps onto one generic state
/// (the Postgres state kept as the detail), each catalog entry becomes a descriptor with its fixes, the
/// scope is the database, and both drivers register through one call.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
public class PostgresCapabilityProviderTests {
  private static readonly string[] _twoDatabases = ["orders-db", "audit-db"];

  private static PostgresExtensionInfo _entry(string name) =>
    PostgresExtensionCatalog.BuiltIn.Single(e => e.Name == name);

  private static ValueTask<NpgsqlConnection> _throwing(IServiceProvider services, CancellationToken _) =>
    throw new InvalidOperationException($"opened with {services.GetType().Name}");

  [Test]
  [Arguments(PostgresExtensionState.Installed, CapabilityState.Ready)]
  [Arguments(PostgresExtensionState.Available, CapabilityState.Provisionable)]
  [Arguments(PostgresExtensionState.NotAllowlisted, CapabilityState.NotPermitted)]
  [Arguments(PostgresExtensionState.NeedsPreload, CapabilityState.NeedsServerChange)]
  [Arguments(PostgresExtensionState.NoPrivilege, CapabilityState.NoPrivilege)]
  [Arguments(PostgresExtensionState.Missing, CapabilityState.Missing)]
  [Arguments(PostgresExtensionState.Unknown, CapabilityState.Unknown)]
  public async Task EachPostgresState_MapsOntoOneGenericStateAsync(PostgresExtensionState postgres, CapabilityState generic) {
    await Assert.That(PostgresCapabilityProbe.ToCapabilityState(postgres)).IsEqualTo(generic);
  }

  [Test]
  public async Task Describe_CarriesIdPurposeFeaturesAndProvisionableAsync() {
    var descriptor = PostgresExtensionCatalog.Describe(_entry("pg_repack"));

    await Assert.That(descriptor.Id).IsEqualTo(new CapabilityId("postgres", "pg_repack"));
    await Assert.That(descriptor.Purpose).IsEqualTo(_entry("pg_repack").Enables);
    await Assert.That(descriptor.Features).IsEquivalentTo(_entry("pg_repack").Features);
    await Assert.That(descriptor.Provisionable).IsTrue();
    await Assert.That(PostgresExtensionCatalog.Describe(_entry("pg_cron")).Provisionable).IsFalse();
  }

  [Test]
  public async Task Describe_WritesEachPrerequisiteAsTextAsync() {
    var squeeze = PostgresExtensionCatalog.Describe(_entry("pg_squeeze")).Prerequisites;
    var repack = PostgresExtensionCatalog.Describe(_entry("pg_repack")).Prerequisites;
    var vector = PostgresExtensionCatalog.Describe(_entry("vector")).Prerequisites;

    await Assert.That(squeeze.Count).IsEqualTo(3);
    await Assert.That(squeeze.Any(p => p.Contains("allowlist", StringComparison.Ordinal))).IsTrue();
    await Assert.That(squeeze.Any(p => p.Contains("shared_preload_libraries", StringComparison.Ordinal))).IsTrue();
    await Assert.That(squeeze.Any(p => p.Contains("wal_level = logical", StringComparison.Ordinal))).IsTrue();
    await Assert.That(repack.Any(p => p.Contains("client program", StringComparison.Ordinal))).IsTrue();
    await Assert.That(vector.Count).IsEqualTo(1);
    await Assert.That(PostgresExtensionCatalog.Describe(
      new PostgresExtensionInfo("my_ext", "something", ["A feature"], true, PostgresExtensionPrerequisites.None)).Prerequisites.Count)
      .IsEqualTo(0).Because("an extension with no prerequisites lists none");
  }

  [Test]
  public async Task Describe_GivesTheFixForEachProblemStateFromThePostgresStateAsync() {
    var squeeze = _entry("pg_squeeze");
    var descriptor = PostgresExtensionCatalog.Describe(squeeze);

    await Assert.That(descriptor.Remedy(CapabilityState.NotPermitted))
      .IsEqualTo(PostgresExtensionCatalog.Remedy(squeeze, PostgresExtensionState.NotAllowlisted));
    await Assert.That(descriptor.Remedy(CapabilityState.NeedsServerChange))
      .IsEqualTo(PostgresExtensionCatalog.Remedy(squeeze, PostgresExtensionState.NeedsPreload));
    await Assert.That(descriptor.Remedy(CapabilityState.NoPrivilege))
      .IsEqualTo(PostgresExtensionCatalog.Remedy(squeeze, PostgresExtensionState.NoPrivilege));
    await Assert.That(descriptor.Remedy(CapabilityState.Missing))
      .IsEqualTo(PostgresExtensionCatalog.Remedy(squeeze, PostgresExtensionState.Missing));
    await Assert.That(descriptor.Remedy(CapabilityState.Unknown))
      .IsEqualTo(PostgresExtensionCatalog.Remedy(squeeze, PostgresExtensionState.Unknown));
  }

  [Test]
  public async Task Registration_OneProviderOverEveryDatabase_ScopesAreTheDatabasesAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangPostgresCapabilities("orders-db", _throwing);
    services.AddWhizbangPostgresCapabilities("audit-db", _throwing);
    services.AddWhizbangPostgresCapabilities("orders-db", _throwing);

    await using var provider = services.BuildServiceProvider();

    var postgres = provider.GetServices<ICapabilityProvider>().Single();
    await Assert.That(postgres.Name).IsEqualTo("postgres");
    await Assert.That(postgres.Scopes).IsEquivalentTo(_twoDatabases);
    await Assert.That(postgres.Descriptors.Count).IsEqualTo(PostgresExtensionCatalog.BuiltIn.Count);
    await Assert.That(provider.GetRequiredService<ICapabilityRegistry>().Statuses.Count)
      .IsEqualTo(2 * PostgresExtensionCatalog.BuiltIn.Count);
    await Assert.That(provider.GetServices<IStartupStep>().OfType<CapabilitiesStartupStep>().Count()).IsEqualTo(1);
    var provisioner = provider.GetServices<ICapabilityProvisioner>().Single();
    await Assert.That(provisioner.Provider).IsEqualTo("postgres")
      .Because("one provisioner creates and upgrades extensions on every registered database");
  }

  [Test]
  public async Task TheProvisionerOpensItsConnectionThroughTheContainer_AndValidatesItsArgumentAsync() {
    await using var provider = new ServiceCollection().AddLogging()
      .AddWhizbangPostgresCapabilities("orders-db", _throwing).BuildServiceProvider();
    var provisioner = provider.GetServices<ICapabilityProvisioner>().Single();
    var status = new CapabilityStatus(PostgresExtensionCatalog.Describe(_entry("pg_trgm")), "orders-db",
      CapabilityState.Provisionable, Version: null, Detail: null, Reason: null);

    await Assert.That(async () => await provisioner.ProvisionAsync(status, CapabilityProvisionAction.Create, CancellationToken.None))
      .Throws<InvalidOperationException>().WithMessageContaining("opened with");
    await Assert.That(async () => await provisioner.ProvisionAsync(null!, CapabilityProvisionAction.Create, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheProbeOpensItsConnectionThroughTheContainerAsync() {
    await using var provider = new ServiceCollection().AddLogging()
      .AddWhizbangPostgresCapabilities("orders-db", _throwing).BuildServiceProvider();
    var registry = provider.GetRequiredService<ICapabilityRegistry>();

    await registry.RefreshAsync(CancellationToken.None);

    await Assert.That(registry.Statuses.All(s => s.State == CapabilityState.Unknown)).IsTrue();
    await Assert.That(registry.Statuses[0].Reason).Contains("opened with");
  }

  [Test]
  public async Task Arguments_AreValidatedAsync() {
    await Assert.That(() => PostgresCapabilityServiceCollectionExtensions.AddWhizbangPostgresCapabilities(null!, "db", _throwing))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangPostgresCapabilities(" ", _throwing))
      .Throws<ArgumentException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangPostgresCapabilities("db", null!))
      .Throws<ArgumentNullException>();
  }
}
