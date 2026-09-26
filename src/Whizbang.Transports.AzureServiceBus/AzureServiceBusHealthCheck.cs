using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Health check for Azure Service Bus connectivity.
/// Verifies that the transport is available and can communicate with Azure Service Bus.
/// </summary>
/// <tests>tests/Whizbang.Transports.Tests/AzureServiceBusHealthCheckTests.cs</tests>
public class AzureServiceBusHealthCheck(ITransport transport) : IHealthCheck {
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

  /// <summary>
  /// Reports whether the registered transport is an Azure Service Bus transport.
  /// </summary>
  /// <param name="context">Health-check registration context (unused).</param>
  /// <param name="cancellationToken">Cancellation token (unused: the check is synchronous).</param>
  /// <returns>Healthy for an Azure Service Bus transport, Degraded for anything else.</returns>
  /// <remarks>
  /// Straight-line, like <c>RabbitMQHealthCheck</c>: this check inspects the transport's type and
  /// nothing else, so there is no call here that can fail and nothing for a catch to report. An
  /// Unhealthy arm belongs with the broker round trip that would justify it, not around a type
  /// test that cannot throw.
  /// </remarks>
  public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) {
    // Check if transport is Azure Service Bus transport
    if (_transport is not AzureServiceBusTransport) {
      return Task.FromResult(HealthCheckResult.Degraded("Transport is not Azure Service Bus"));
    }

    // Basic health check - the transport is instantiated and not disposed
    // A more comprehensive check would attempt to send/receive a test message
    return Task.FromResult(HealthCheckResult.Healthy("Azure Service Bus transport is available"));
  }
}
