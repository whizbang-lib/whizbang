using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Transports;

/// <summary>
/// Runs manifest-driven provisioning in EVERY TransportNamespace (transport traffic classes,
/// topology arc phase 8): one inner provisioner per namespace, each holding that namespace's
/// management plane.
/// </summary>
/// <remarks>
/// <para>
/// The topology manifest is namespace-INDEPENDENT by construction — a TransportNamespace
/// changes which broker an entity lives in, never what it is called — so the same manifest is
/// provisioned into each namespace. That is exactly what makes the consume-side mirror
/// possible: the entity a class namespace subscribes is the same entity name the default
/// namespace subscribes.
/// </para>
/// <para>
/// <see cref="EnsureTopicExistsAsync"/> is the exception: it is the publish-side, single-entity
/// call and carries no namespace key, so it runs in the DEFAULT namespace only. Class-namespace
/// publish entities are minted by that namespace's own transport on first send.
/// </para>
/// </remarks>
/// <docs>messaging/transports/transports#transport-namespaces</docs>
/// <tests>tests/Whizbang.Core.Tests/Transports/CompositeInfrastructureProvisionerTests.cs</tests>
public sealed class CompositeInfrastructureProvisioner : IInfrastructureProvisioner {
  private readonly IReadOnlyList<IInfrastructureProvisioner> _provisioners;

  /// <summary>
  /// Creates the composition.
  /// </summary>
  /// <param name="provisioners">
  /// One provisioner per TransportNamespace, DEFAULT FIRST — the order the namespace-routing
  /// transport reports its namespaces in.
  /// </param>
  /// <exception cref="ArgumentException"><paramref name="provisioners"/> is empty.</exception>
  public CompositeInfrastructureProvisioner(IReadOnlyList<IInfrastructureProvisioner> provisioners) {
    ArgumentNullException.ThrowIfNull(provisioners);

    if (provisioners.Count == 0) {
      throw new ArgumentException(
        "A composite provisioner needs at least the default TransportNamespace's provisioner — an empty "
        + "composition would silently provision nothing, which reads as success while every subscribe fails.",
        nameof(provisioners));
    }

    _provisioners = provisioners;
  }

  /// <inheritdoc />
  public async Task ProvisionOwnedDomainsAsync(
      IReadOnlySet<string> ownedDomains, CancellationToken cancellationToken = default) {
    foreach (var provisioner in _provisioners) {
      await provisioner.ProvisionOwnedDomainsAsync(ownedDomains, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public Task EnsureTopicExistsAsync(string topicName, CancellationToken cancellationToken = default) =>
    _provisioners[0].EnsureTopicExistsAsync(topicName, cancellationToken);

  /// <inheritdoc />
  public async Task ProvisionManifestAsync(
      TopologyManifest manifest, CancellationToken cancellationToken = default) {
    foreach (var provisioner in _provisioners) {
      await provisioner.ProvisionManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
    }
  }
}
