using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Tags;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Azure Service Bus's admin-plane backlog peek (topology arc phase 10): subscription DEPTH from
/// <c>GetSubscriptionRuntimeProperties</c> and oldest-enqueue AGE from a single head peek, per
/// entity per duty tick.
/// </summary>
/// <remarks>
/// <para>
/// Depth alone would not have distinguished the two backlog shapes the motivating incident
/// produced. Age does, and Service Bus is the transport that can supply it: the broker stamps
/// <c>EnqueuedTime</c> and it survives every redelivery, which is the same property phase 8.5's
/// age-based poison detection rides. A head peek is a read that does not lock, settle, or count
/// against delivery — the cheapest possible way to ask "how old is the front of this queue".
/// </para>
/// <para>
/// The peek walks the transport's OWN liveness registry of live (topic, subscription) pairs, so
/// the duty samples exactly what this instance consumes from and nothing else; under multi-
/// namespace routing it fans across every namespace peer, tagging each sample with the namespace
/// it came from, because a per-namespace quota is the thing an operator acts on.
/// </para>
/// </remarks>
/// <docs>operations/observability/managed-resource-health#backlog-age</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbBacklogPeekTests.cs</tests>
public sealed class AsbBacklogPeek : IBacklogPeek {
  private readonly ITransport _transport;
  private readonly TagOptions? _tagOptions;

  /// <summary>Creates the peek over the host's transport (routing peer or single instance).</summary>
  /// <param name="transport">The registered transport.</param>
  /// <param name="tagOptions">Tag options, so a namespace can be labelled with the traffic class
  /// routed to it; null ⇒ every sample reports the unclassified domain class.</param>
  /// <exception cref="ArgumentNullException">Thrown when transport is null.</exception>
  public AsbBacklogPeek(ITransport transport, TagOptions? tagOptions = null) {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _tagOptions = tagOptions;
  }

  /// <inheritdoc />
  public string TransportName => "asb";

  /// <inheritdoc />
  public async Task<IReadOnlyList<BacklogSample>> PeekAsync(CancellationToken cancellationToken) {
    var samples = new List<BacklogSample>();

    foreach (var (namespaceKey, transport) in _namespacePeers()) {
      if (transport is not AzureServiceBusTransport asb) {
        continue;
      }

      var trafficClass = _trafficClassFor(namespaceKey);
      foreach (var sample in await asb.PeekBacklogsAsync(cancellationToken).ConfigureAwait(false)) {
        samples.Add(sample with {
          Transport = TransportName,
          TransportNamespace = namespaceKey,
          TrafficClass = trafficClass,
        });
      }
    }

    return samples;
  }

  private IEnumerable<(string NamespaceKey, ITransport Transport)> _namespacePeers() {
    if (_transport is NamespaceRoutingTransport router) {
      return router.NamespaceKeys.Select(key => (key, router.Resolve(key)));
    }

    return [(TransportNamespaces.DefaultKey, _transport)];
  }

  /// <summary>
  /// The traffic class routed to <paramref name="namespaceKey"/> — the tag bound to it, or the
  /// unclassified domain class. A reverse lookup is enough because startup validation guarantees a
  /// message type maps to at most one namespace key.
  /// </summary>
  private string _trafficClassFor(string namespaceKey) {
    if (_tagOptions is null) {
      return TrafficClasses.DOMAIN;
    }

    foreach (var (tag, key) in _tagOptions.RouteNamespaceBindings) {
      if (string.Equals(key, namespaceKey, StringComparison.Ordinal)) {
        return tag;
      }
    }

    return TrafficClasses.DOMAIN;
  }
}
