// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.PostgresExtensions;

/// <summary>
/// Registration of the probe: one capabilities singleton over every registered database, one startup
/// step, and <c>Whizbang:PostgresExtensions</c> bound with <c>ProbeOnStartup</c> defaulting to true.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
public class PostgresExtensionServiceCollectionExtensionsTests {
  private static readonly string[] _twoDatabases = ["orders-db", "audit-db"];

  private static ValueTask<NpgsqlConnection> _throwing(IServiceProvider services, CancellationToken _) =>
    throw new InvalidOperationException($"opened with {services.GetType().Name}");

  private static ServiceCollection _services(IConfiguration? configuration = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddMetrics();
    if (configuration is not null) {
      services.AddSingleton(configuration);
    }
    return services;
  }

  [Test]
  public async Task ProbeOnStartup_DefaultsToTrueAsync() {
    await using var provider = _services().AddWhizbangPostgresExtensionProbe("orders-db", _throwing).BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<PostgresExtensionsOptions>>().Value.ProbeOnStartup).IsTrue();
    await Assert.That(new PostgresExtensionsOptions().ProbeOnStartup).IsTrue();
  }

  [Test]
  public async Task ProbeOnStartup_BindsFromConfigurationAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Whizbang:PostgresExtensions:ProbeOnStartup"] = "false" })
      .Build();

    await using var provider = _services(configuration).AddWhizbangPostgresExtensionProbe("orders-db", _throwing).BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<PostgresExtensionsOptions>>().Value.ProbeOnStartup).IsFalse();
  }

  [Test]
  public async Task CodeConfiguration_KeepsItsValueWhenTheKeyIsAbsentAsync() {
    var services = _services(new ConfigurationBuilder().Build());
    services.Configure<PostgresExtensionsOptions>(o => o.ProbeOnStartup = false);

    await using var provider = services.AddWhizbangPostgresExtensionProbe("orders-db", _throwing).BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<PostgresExtensionsOptions>>().Value.ProbeOnStartup).IsFalse();
  }

  [Test]
  public async Task Registers_OneCapabilitiesOverEveryDatabase_AndOneStepAsync() {
    var services = _services();
    services.AddWhizbangPostgresExtensionProbe("orders-db", _throwing);
    services.AddWhizbangPostgresExtensionProbe("audit-db", _throwing);
    services.AddWhizbangPostgresExtensionProbe("orders-db", _throwing);

    await using var provider = services.BuildServiceProvider();

    var capabilities = provider.GetRequiredService<IPostgresCapabilities>();
    await Assert.That(capabilities.Databases.Select(d => d.Database)).IsEquivalentTo(_twoDatabases);
    await Assert.That(provider.GetServices<IStartupStep>().OfType<PostgresExtensionsStartupStep>().Count()).IsEqualTo(1);
    await Assert.That(capabilities).IsTypeOf<PostgresCapabilities>();
    await Assert.That(((PostgresCapabilities)capabilities).Metrics).IsNotNull()
      .Because("registration creates the gauge with the capabilities");
  }

  [Test]
  public async Task TheProbeOpensItsConnectionThroughTheContainerAsync() {
    await using var provider = _services().AddWhizbangPostgresExtensionProbe("orders-db", _throwing).BuildServiceProvider();
    var capabilities = provider.GetRequiredService<IPostgresCapabilities>();

    await capabilities.RefreshAsync(CancellationToken.None);

    await Assert.That(capabilities.Databases.Single().ProbeFailure).Contains("opened with");
  }

  [Test]
  public async Task Arguments_AreValidatedAsync() {
    await Assert.That(() => PostgresExtensionServiceCollectionExtensions.AddWhizbangPostgresExtensionProbe(null!, "db", _throwing))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangPostgresExtensionProbe(" ", _throwing))
      .Throws<ArgumentException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangPostgresExtensionProbe("db", null!))
      .Throws<ArgumentNullException>();
  }
}
