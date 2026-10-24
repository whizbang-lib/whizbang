// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Startup;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// <c>AddWhizbangCapabilities</c>: one registry over every registered provider, one startup step, the
/// gauge, and <c>Whizbang:Capabilities</c> bound with <c>ProbeOnStartup</c> defaulting to true.
/// </summary>
/// <docs>operations/infrastructure/capabilities#configuration</docs>
public class CapabilityServiceCollectionExtensionsTests {
  private static readonly string[] _providers = ["postgres", "broker"];

  private static ServiceCollection _services(IConfiguration? configuration = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    if (configuration is not null) {
      services.AddSingleton(configuration);
    }
    return services;
  }

  [Test]
  public async Task ProbeOnStartup_DefaultsToTrue_WithoutConfigurationAsync() {
    await using var provider = _services().AddWhizbangCapabilities().BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<CapabilityOptions>>().Value.ProbeOnStartup).IsTrue();
  }

  [Test]
  public async Task ProbeOnStartup_BindsFromConfigurationAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Whizbang:Capabilities:ProbeOnStartup"] = "false" })
      .Build();

    await using var provider = _services(configuration).AddWhizbangCapabilities().BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<CapabilityOptions>>().Value.ProbeOnStartup).IsFalse();
  }

  [Test]
  public async Task CodeConfiguration_KeepsItsValueWhenTheKeyIsAbsentAsync() {
    var services = _services(new ConfigurationBuilder().Build());
    services.Configure<CapabilityOptions>(o => o.ProbeOnStartup = false);

    await using var provider = services.AddWhizbangCapabilities().BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<CapabilityOptions>>().Value.ProbeOnStartup).IsFalse();
  }

  [Test]
  public async Task Registers_OneRegistryOverEveryProvider_AndOneStep_HoweverOftenCalledAsync() {
    var services = _services();
    services.AddSingleton<ICapabilityProvider>(new FakeCapabilityProvider("postgres", ["orders-db"], FakeCapabilityProvider.Describe("postgres", "vector")));
    services.AddSingleton<ICapabilityProvider>(new FakeCapabilityProvider("broker", ["vhost-a"], FakeCapabilityProvider.Describe("broker", "delayed_exchange")));
    services.AddWhizbangCapabilities();
    services.AddWhizbangCapabilities();

    await using var provider = services.BuildServiceProvider();

    var registry = provider.GetRequiredService<ICapabilityRegistry>();
    await Assert.That(registry.Providers.Select(p => p.Name)).IsEquivalentTo(_providers);
    await Assert.That(provider.GetServices<IStartupStep>().OfType<CapabilitiesStartupStep>().Count()).IsEqualTo(1);
    await Assert.That(((CapabilityRegistry)registry).Metrics).IsNotNull()
      .Because("registration creates the gauge with the registry");
  }

  [Test]
  public async Task Registers_TheManifestSource_TheEnforcer_AndOneHealthEntry_HoweverOftenCalledAsync() {
    var services = _services();
    services.AddSingleton<IHostApplicationLifetime>(new Microsoft.Extensions.Hosting.Internal.ApplicationLifetime(
      Microsoft.Extensions.Logging.Abstractions.NullLogger<Microsoft.Extensions.Hosting.Internal.ApplicationLifetime>.Instance));
    services.AddWhizbangCapabilities();
    services.AddWhizbangCapabilities();

    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetServices<ICapabilityRequirementSource>().OfType<ConfigurationManifestRequirementSource>().Count()).IsEqualTo(1);
    await Assert.That(provider.GetServices<IHostedService>().OfType<CapabilityVerdictEnforcer>().Count()).IsEqualTo(1)
      .Because("one enforcer, however often capabilities are added");
    var registrations = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value.Registrations;
    var entry = registrations.Single(r => r.Name == CapabilityHealthCheck.NAME);
    await Assert.That(entry.Factory(provider)).IsTypeOf<CapabilityHealthCheck>();
  }

  [Test]
  public async Task HostedServices_ResolveInAContainerWithoutAHostAsync() {
    await using var provider = _services().AddWhizbangCapabilities().BuildServiceProvider();

    await Assert.That(provider.GetServices<IHostedService>().OfType<CapabilityVerdictEnforcer>().Count()).IsEqualTo(1)
      .Because("a composition resolved without a host (a tool, a test fixture) has no application lifetime until it must stop");
  }

  [Test]
  public async Task StopApplication_StopsTheHostsLifetime_WhenCalledAsync() {
    var lifetime = new Microsoft.Extensions.Hosting.Internal.ApplicationLifetime(
      Microsoft.Extensions.Logging.Abstractions.NullLogger<Microsoft.Extensions.Hosting.Internal.ApplicationLifetime>.Instance);
    var services = _services();
    services.AddSingleton<IHostApplicationLifetime>(lifetime);
    await using var provider = services.BuildServiceProvider();

    CapabilityVerdictEnforcer.StopApplicationOf(provider)();

    await Assert.That(lifetime.ApplicationStopping.IsCancellationRequested).IsTrue();
  }

  [Test]
  public async Task Arguments_AreValidatedAsync() {
    await Assert.That(() => CapabilityServiceCollectionExtensions.AddWhizbangCapabilities(null!))
      .Throws<ArgumentNullException>();
  }
}
