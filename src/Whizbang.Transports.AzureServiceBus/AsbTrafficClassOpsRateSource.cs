using System;
using System.Collections.Generic;
using Whizbang.Core.Tags;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Publishes the transport's own idle ops-rate projection PER TransportNamespace (topology arc
/// phase 10, spec increment 5).
/// </summary>
/// <remarks>
/// The self-check already computes this number to decide whether to degrade health; this makes it
/// a gauge, tagged by namespace and traffic class. Per-namespace, never summed: each namespace has
/// its own credit pool, so a sum answers no question an operator can act on — the whole reason
/// <see cref="AsbOpsRateHealthSource"/> reports the WORST namespace rather than the total.
/// </remarks>
/// <docs>operations/observability/metrics#traffic-classes</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbTrafficClassOpsRateSourceTests.cs</tests>
public sealed class AsbTrafficClassOpsRateSource : ITrafficClassOpsRateSource {
  private readonly ITransport _transport;
  private readonly TagOptions? _tagOptions;

  /// <summary>Creates the source over the host's transport.</summary>
  /// <param name="transport">The registered transport (routing peer or single instance).</param>
  /// <param name="tagOptions">Tag options, so a namespace carries the class routed to it.</param>
  /// <exception cref="ArgumentNullException">Thrown when transport is null.</exception>
  public AsbTrafficClassOpsRateSource(ITransport transport, TagOptions? tagOptions = null) {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _tagOptions = tagOptions;
  }

  /// <inheritdoc />
  public string TransportName => "asb";

  /// <inheritdoc />
  public IReadOnlyList<TrafficClassOpsRate> Project() {
    var rates = new List<TrafficClassOpsRate>();

    if (_transport is NamespaceRoutingTransport router) {
      foreach (var key in router.NamespaceKeys) {
        _add(rates, key, router.Resolve(key));
      }
    } else {
      _add(rates, TransportNamespaces.DefaultKey, _transport);
    }

    return rates;
  }

  private void _add(List<TrafficClassOpsRate> rates, string namespaceKey, ITransport transport) {
    if (transport is not AzureServiceBusTransport asb
        || asb.IdleOpsRateProjection is not { } projection) {
      return;
    }

    rates.Add(new TrafficClassOpsRate(
      namespaceKey, _trafficClassFor(namespaceKey), projection.ProjectedIdleOpsPerSecond));
  }

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
