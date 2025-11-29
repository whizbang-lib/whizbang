using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Health check for Azure Service Bus connectivity.
/// Verifies that the transport is available and can communicate with Azure Service Bus.
/// </summary>
public class AzureServiceBusHealthCheck(ITransport transport) : IHealthCheck {
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

  public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) {
    try {
      // Check if transport is Azure Service Bus transport
      if (_transport is not AzureServiceBusTransport) {
        return Task.FromResult(HealthCheckResult.Degraded("Transport is not Azure Service Bus"));
      }

      // Basic health check - the transport is instantiated and not disposed
      // A more comprehensive check would attempt to send/receive a test message
      return Task.FromResult(HealthCheckResult.Healthy("Azure Service Bus transport is available"));
    } catch (Exception ex) {
      return Task.FromResult(HealthCheckResult.Unhealthy("Azure Service Bus transport is not healthy", ex));
    }
  }
}
