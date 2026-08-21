using System.Globalization;
using Whizbang.Core.Health;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Managed-health surface of the idle ops-rate self-check: reports the <c>"transport"</c>
/// component as <see cref="ComponentState.Degraded"/> — never faulted, the transport still
/// serves — while the transport's projected idle broker-operation rate exceeds
/// <see cref="AzureServiceBusOptions.OpsRateWarningThresholdPerSecond"/>. The projection is the
/// receive machinery's own idle spend (session accept churn), a failure mode that is invisible
/// to message-level metrics and, before this source, only produced a log line. Recovery is
/// automatic: the transport re-projects on every subscribe and whenever adaptive acceptors
/// resize a pool, so a decayed pool clears the degradation without a redeploy.
/// </summary>
/// <remarks>
/// Multi-namespace hosts (transport traffic classes, topology arc phase 8) report the WORST
/// namespace, never the sum: the threshold is a PER-NAMESPACE request budget, so each
/// TransportNamespace projects against its own quota. Adding namespaces must never degrade a
/// fleet that is healthy in every one of them.
/// </remarks>
/// <docs>messaging/transports/azure-service-bus#ops-rate-self-check</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbOpsRateHealthSourceTests.cs</tests>
public sealed class AsbOpsRateHealthSource : IWhizbangHealthSource {
  private readonly ITransport _transport;

  /// <summary>
  /// Creates the source over the registered transport. A non-ASB transport always reports
  /// operational — the self-check only understands ASB session-accept economics.
  /// </summary>
  public AsbOpsRateHealthSource(ITransport transport) {
    ArgumentNullException.ThrowIfNull(transport);
    _transport = transport;
  }

  /// <inheritdoc />
  public string Component => "transport";

  /// <inheritdoc />
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
    var projection = _worstProjection();
    if (projection is not { ExceedsThreshold: true } exceeded) {
      return ValueTask.FromResult(new ComponentHealth(ComponentState.Operational));
    }

    var detail = string.Create(
      CultureInfo.InvariantCulture,
      $"idle ops-rate self-check: receive machinery projects {exceeded.ProjectedIdleOpsPerSecond:F1} idle broker ops/sec, over the {exceeded.ThresholdPerSecond:F0}/sec warning threshold — idle accept churn alone is consuming a material share of the namespace request quota");
    return ValueTask.FromResult(new ComponentHealth(ComponentState.Degraded, detail));
  }

  /// <summary>
  /// The highest projection across every TransportNamespace this transport spans (one entry for
  /// a single-namespace host). Deliberately a MAX and not a SUM — see the type remarks.
  /// </summary>
  private AsbOpsRateProjection? _worstProjection() {
    AsbOpsRateProjection? worst = null;
    foreach (var transport in _namespaceTransports()) {
      if (transport is AzureServiceBusTransport asb
          && asb.IdleOpsRateProjection is { } projection
          && (worst is null || projection.ProjectedIdleOpsPerSecond > worst.Value.ProjectedIdleOpsPerSecond)) {
        worst = projection;
      }
    }

    return worst;
  }

  private IEnumerable<ITransport> _namespaceTransports() =>
    _transport is NamespaceRoutingTransport router ? router.Transports : [_transport];
}
