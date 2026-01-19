using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Health check for RabbitMQ connectivity.
/// Verifies that the transport is available and can communicate with RabbitMQ.
/// </summary>
/// <docs>components/transports/rabbitmq</docs>
public class RabbitMQHealthCheck : IHealthCheck {
  private readonly ITransport _transport;
  private readonly IConnection _connection;

  /// <summary>
  /// Initializes a new instance of RabbitMQHealthCheck.
  /// </summary>
  /// <param name="transport">The transport instance to check.</param>
  /// <param name="connection">The RabbitMQ connection to verify.</param>
  public RabbitMQHealthCheck(ITransport transport, IConnection connection) {
    ArgumentNullException.ThrowIfNull(transport);
    ArgumentNullException.ThrowIfNull(connection);

    _transport = transport;
    _connection = connection;
  }

  /// <inheritdoc />
  public Task<HealthCheckResult> CheckHealthAsync(
    HealthCheckContext context,
    CancellationToken cancellationToken = default
  ) {
    // Check if transport is the correct type
    if (_transport is not RabbitMQTransport) {
      return Task.FromResult(HealthCheckResult.Degraded(
        "Transport is not a RabbitMQ transport"
      ));
    }

    // Check if connection is open
    if (!_connection.IsOpen) {
      return Task.FromResult(HealthCheckResult.Unhealthy(
        "RabbitMQ connection is not open"
      ));
    }

    // All checks passed
    return Task.FromResult(HealthCheckResult.Healthy(
      "RabbitMQ transport is healthy"
    ));
  }
}
