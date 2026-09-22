using TUnit.Core;
using Whizbang.Core.Transports;

#pragma warning disable CA1707

namespace Whizbang.Core.Tests.Transports;

public class InfrastructureProvisionerTests {
  [Test]
  public async Task EnsureTopicExistsAsync_DefaultImplementation_CompletesWithoutThrowingAsync() {
    // Transports that need no pre-creation (e.g. RabbitMQ) inherit this default. "No-op" has to
    // mean it neither performs work nor falls back to the owned-domains surface — a default that
    // quietly routed here would re-provision every owned domain on every publish.
    var provisioner = new CountingProvisioner();

    // Act
    var task = ((IInfrastructureProvisioner)provisioner).EnsureTopicExistsAsync("test-topic");
    await task;

    // Assert
    await Assert.That(task.IsCompletedSuccessfully).IsTrue()
      .Because("the default is a completed task, not deferred work");
    await Assert.That(provisioner.OwnedDomainCalls).IsEqualTo(0);
  }

  [Test]
  public async Task ProvisionManifestAsync_DefaultImplementation_IsNoOpAsync() {
    // Custom provisioners written before the topology arc implement only the owned-domains
    // surface — the manifest-driven DARK provisioning seam (phase 5) defaults to a no-op so
    // they keep compiling and keep their existing behavior.
    var provisioner = new CountingProvisioner();
    var manifest = new Whizbang.Core.Routing.TopologyManifest("svc", [], []);

    // Act
    var task = ((IInfrastructureProvisioner)provisioner).ProvisionManifestAsync(manifest);
    await task;

    // Assert - nothing happened: no work deferred, and the pre-topology surface untouched
    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await Assert.That(provisioner.OwnedDomainCalls).IsEqualTo(0)
      .Because("an implementer that never overrode ProvisionManifestAsync must keep its existing behavior");
  }

  /// <summary>
  /// Implements only the required member, so the other two resolve to the interface's default
  /// implementations, and counts calls to it so "the default did nothing" is observable.
  /// </summary>
  private sealed class CountingProvisioner : IInfrastructureProvisioner {
    public int OwnedDomainCalls { get; private set; }

    public Task ProvisionOwnedDomainsAsync(IReadOnlySet<string> ownedDomains, CancellationToken cancellationToken = default) {
      OwnedDomainCalls++;
      return Task.CompletedTask;
    }
  }
}
