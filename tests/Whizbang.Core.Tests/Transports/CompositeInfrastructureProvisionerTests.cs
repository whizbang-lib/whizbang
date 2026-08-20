using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tests for <see cref="CompositeInfrastructureProvisioner"/> — manifest provisioning across
/// every TransportNamespace (transport traffic classes, topology arc phase 8). The manifest is
/// namespace-INDEPENDENT by construction (a namespace changes which broker, never the entity
/// name), so the same manifest is provisioned into each namespace's management plane.
/// </summary>
[Category("Core")]
[Category("Transports")]
public class CompositeInfrastructureProvisionerTests {
  private static readonly string[] _recordsTopic = ["myapp.records"];

  [Test]
  public async Task ProvisionManifestAsync_RunsInEveryNamespaceAsync() {
    var @default = new RecordingProvisioner();
    var bulk = new RecordingProvisioner();
    var composite = new CompositeInfrastructureProvisioner([@default, bulk]);
    var manifest = _manifest();

    await composite.ProvisionManifestAsync(manifest);

    await Assert.That(@default.Manifests.Single()).IsSameReferenceAs(manifest);
    await Assert.That(bulk.Manifests.Single()).IsSameReferenceAs(manifest)
      .Because("the same entity set exists in each namespace — the manifest is namespace-independent");
  }

  [Test]
  public async Task ProvisionOwnedDomainsAsync_RunsInEveryNamespaceAsync() {
    var @default = new RecordingProvisioner();
    var bulk = new RecordingProvisioner();
    var composite = new CompositeInfrastructureProvisioner([@default, bulk]);

    await composite.ProvisionOwnedDomainsAsync(new HashSet<string> { "myapp.orders" });

    await Assert.That(@default.OwnedDomainCalls).IsEqualTo(1);
    await Assert.That(bulk.OwnedDomainCalls).IsEqualTo(1);
  }

  [Test]
  public async Task EnsureTopicExistsAsync_RunsInTheDefaultNamespaceOnlyAsync() {
    // A publish-side topic belongs to the namespace the publisher will actually send to; the
    // composite has no namespace key here, so it must not mint the entity everywhere.
    var @default = new RecordingProvisioner();
    var bulk = new RecordingProvisioner();
    var composite = new CompositeInfrastructureProvisioner([@default, bulk]);

    await composite.EnsureTopicExistsAsync("myapp.records");

    await Assert.That(@default.Topics).IsEquivalentTo(_recordsTopic);
    await Assert.That(bulk.Topics.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Constructor_NoProvisioners_ThrowsAsync() {
    await Assert.That(() => new CompositeInfrastructureProvisioner([])).Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_NullProvisioners_ThrowsAsync() {
    await Assert.That(() => new CompositeInfrastructureProvisioner(null!)).Throws<ArgumentNullException>();
  }

  private static TopologyManifest _manifest() => new("orders-service", [], []);

  private sealed class RecordingProvisioner : IInfrastructureProvisioner {
    public List<TopologyManifest> Manifests { get; } = [];
    public List<string> Topics { get; } = [];
    public int OwnedDomainCalls { get; private set; }

    public Task ProvisionOwnedDomainsAsync(
        IReadOnlySet<string> ownedDomains, CancellationToken cancellationToken = default) {
      OwnedDomainCalls++;
      return Task.CompletedTask;
    }

    public Task EnsureTopicExistsAsync(string topicName, CancellationToken cancellationToken = default) {
      Topics.Add(topicName);
      return Task.CompletedTask;
    }

    public Task ProvisionManifestAsync(TopologyManifest manifest, CancellationToken cancellationToken = default) {
      Manifests.Add(manifest);
      return Task.CompletedTask;
    }
  }
}
