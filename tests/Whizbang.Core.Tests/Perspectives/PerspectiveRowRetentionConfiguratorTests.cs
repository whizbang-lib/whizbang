using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Unit tests for <see cref="PerspectiveRowRetentionConfigurator"/> — the hosted startup step
/// that applies <see cref="PerspectiveRowRetentionOptions"/> to the TTL registry.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
[NotInParallel("PerspectiveTtlRegistryRuntimeConfig")]
public class PerspectiveRowRetentionConfiguratorTests {
  private sealed class ConfiguredModel;

  [Test]
  public async Task StartAsync_AppliesOverridesToTheRegistryAsync() {
    PerspectiveTtlRegistry.Register(typeof(ConfiguredModel), 3600);
    try {
      var options = new PerspectiveRowRetentionOptions();
      options.Overrides[typeof(ConfiguredModel).FullName!] = 60;
      var configurator = new PerspectiveRowRetentionConfigurator(
        Options.Create(options), NullLogger<PerspectiveRowRetentionConfigurator>.Instance);

      await configurator.StartAsync(CancellationToken.None);

      await Assert.That(PerspectiveTtlRegistry.ResolveSeconds(typeof(ConfiguredModel))).IsEqualTo(60)
        .Because("startup applies the operator overrides to the registry");
    } finally {
      PerspectiveTtlRegistry.ApplyRuntimeConfiguration(enabled: true, overrides: null);
    }
  }

  [Test]
  public async Task StartAsync_Disabled_TurnsRetentionOffAsync() {
    PerspectiveTtlRegistry.Register(typeof(ConfiguredModel), 3600);
    try {
      var configurator = new PerspectiveRowRetentionConfigurator(
        Options.Create(new PerspectiveRowRetentionOptions { Enabled = false }),
        NullLogger<PerspectiveRowRetentionConfigurator>.Instance);

      await configurator.StartAsync(CancellationToken.None);

      await Assert.That(PerspectiveTtlRegistry.ResolveSeconds(typeof(ConfiguredModel))).IsEqualTo(-1)
        .Because("the kill switch resolves every model to no-TTL");
    } finally {
      PerspectiveTtlRegistry.ApplyRuntimeConfiguration(enabled: true, overrides: null);
    }
  }
}
