using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Readiness check for RabbitMQ transport.
/// Verifies that the RabbitMQ connection is established and ready to accept messages.
/// Logs at Warning level when the connection transitions to closed (once, not per check).
/// </summary>
/// <docs>messaging/transports/rabbitmq</docs>
public sealed partial class RabbitMQReadinessCheck : ITransportReadinessCheck {
  private readonly IConnection _connection;
  private readonly ILogger _logger;
  private bool _wasClosedLastCheck;

  /// <summary>
  /// Initializes a new instance of RabbitMQReadinessCheck.
  /// </summary>
  public RabbitMQReadinessCheck(IConnection connection, ILogger<RabbitMQReadinessCheck>? logger = null) {
    ArgumentNullException.ThrowIfNull(connection);
    _connection = connection;
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RabbitMQReadinessCheck>.Instance;
  }

  /// <inheritdoc />
  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    var isOpen = _connection.IsOpen;

    if (!isOpen && !_wasClosedLastCheck) {
      LogConnectionClosed(_logger, _connection.CloseReason?.ReplyText ?? "unknown");
      _wasClosedLastCheck = true;
    } else if (isOpen && _wasClosedLastCheck) {
      LogConnectionRecovered(_logger);
      _wasClosedLastCheck = false;
    }

    return Task.FromResult(isOpen);
  }

  [LoggerMessage(Level = LogLevel.Error, Message = "RabbitMQ publisher connection is NOT open — all outbox publishing is blocked. CloseReason: {CloseReason}")]
  private static partial void LogConnectionClosed(ILogger logger, string closeReason);

  [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ publisher connection recovered — publishing will resume")]
  private static partial void LogConnectionRecovered(ILogger logger);
}
