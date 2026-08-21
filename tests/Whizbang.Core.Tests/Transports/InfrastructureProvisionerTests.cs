using TUnit.Core;
using Whizbang.Core.Transports;

#pragma warning disable CA1707

namespace Whizbang.Core.Tests.Transports;

public class InfrastructureProvisionerTests {
  [Test]
  public async Task EnsureTopicExistsAsync_DefaultImplementation_CompletesWithoutThrowingAsync() {
    // Arrange
    IInfrastructureProvisioner provisioner = new NoOpProvisioner();

    // Act & Assert - should complete without throwing
    await provisioner.EnsureTopicExistsAsync("test-topic");
  }

  [Test]
  public async Task ProvisionManifestAsync_DefaultImplementation_IsNoOpAsync() {
    // Custom provisioners written before the topology arc implement only the owned-domains
    // surface — the manifest-driven DARK provisioning seam (phase 5) defaults to a no-op so
    // they keep compiling and keep their existing behavior.
    IInfrastructureProvisioner provisioner = new NoOpProvisioner();
    var manifest = new Whizbang.Core.Routing.TopologyManifest("svc", [], []);

    // Act & Assert - should complete without throwing
    await provisioner.ProvisionManifestAsync(manifest);
  }

  private sealed class NoOpProvisioner : IInfrastructureProvisioner {
    public Task ProvisionOwnedDomainsAsync(IReadOnlySet<string> ownedDomains, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }
}
