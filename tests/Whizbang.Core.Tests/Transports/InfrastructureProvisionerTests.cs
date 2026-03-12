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

  private sealed class NoOpProvisioner : IInfrastructureProvisioner {
    public Task ProvisionOwnedDomainsAsync(IReadOnlySet<string> ownedDomains, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }
}
