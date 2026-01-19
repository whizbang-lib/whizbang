using RabbitMQ.Client;
using Whizbang.Core.Transports;

namespace Whizbang.Hosting.RabbitMQ;

/// <summary>
/// Readiness check for RabbitMQ transport.
/// Verifies that the RabbitMQ connection is established and ready to accept messages.
/// </summary>
/// <docs>components/hosting/rabbitmq</docs>
public class RabbitMQReadinessCheck : ITransportReadinessCheck {
  private readonly IConnection _connection;

  /// <summary>
  /// Initializes a new instance of RabbitMQReadinessCheck.
  /// </summary>
  /// <param name="connection">The RabbitMQ connection to check.</param>
  public RabbitMQReadinessCheck(IConnection connection) {
    ArgumentNullException.ThrowIfNull(connection);
    _connection = connection;
  }

  /// <inheritdoc />
  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    // Simple, fast check - just return the connection status
    // This is lightweight as recommended by ITransportReadinessCheck documentation
    return Task.FromResult(_connection.IsOpen);
  }
}
