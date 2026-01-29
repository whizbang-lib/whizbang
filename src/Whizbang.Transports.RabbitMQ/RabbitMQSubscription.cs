using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// RabbitMQ implementation of ISubscription.
/// Manages subscription lifecycle (pause, resume, dispose) for a RabbitMQ consumer.
/// </summary>
/// <docs>components/transports/rabbitmq</docs>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Subscription lifecycle management - simple logging where LoggerMessage overhead isn't justified")]
public sealed class RabbitMQSubscription : ISubscription {
  private readonly IChannel _channel;
  private readonly string _queueName;
  private readonly string? _consumerTag;
  private readonly ILogger? _logger;
  private bool _isActive = true;
  private bool _disposed;

  /// <summary>
  /// Initializes a new instance of RabbitMQSubscription.
  /// </summary>
  /// <param name="channel">RabbitMQ channel used by this consumer</param>
  /// <param name="queueName">Queue name for this subscription</param>
  /// <param name="consumerTag">Consumer tag for this subscription</param>
  /// <param name="logger">Optional logger instance</param>
  public RabbitMQSubscription(
    IChannel channel,
    string queueName,
    string? consumerTag = null,
    ILogger? logger = null
  ) {
    ArgumentNullException.ThrowIfNull(channel);
    ArgumentNullException.ThrowIfNull(queueName);

    _channel = channel;
    _queueName = queueName;
    _consumerTag = consumerTag;
    _logger = logger;
  }

  /// <inheritdoc />
  public bool IsActive => _isActive && !_disposed;

  /// <inheritdoc />
  public Task PauseAsync() {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (!_isActive) {
      _logger?.LogDebug("Subscription for queue {QueueName} already paused, skipping", _queueName);
      return Task.CompletedTask;
    }

    _isActive = false;
    _logger?.LogInformation("Paused subscription for queue {QueueName}", _queueName);

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task ResumeAsync() {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (_isActive) {
      _logger?.LogDebug("Subscription for queue {QueueName} already active, skipping", _queueName);
      return Task.CompletedTask;
    }

    _isActive = true;
    _logger?.LogInformation("Resumed subscription for queue {QueueName}", _queueName);

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (_disposed) {
      return;
    }

    _disposed = true;

    // Fire-and-forget disposal to avoid blocking on RabbitMQ channel cleanup
    // Channel cleanup can block if the broker is slow to respond
    _ = Task.Run(async () => {
      try {
        // Cancel consumer explicitly if consumer tag is available
        // Use noWait: true to avoid waiting for server confirmation
        if (_consumerTag != null) {
          await _channel.BasicCancelAsync(_consumerTag, noWait: true);
          _logger?.LogDebug("Cancelled consumer {ConsumerTag} for queue {QueueName}", _consumerTag, _queueName);
        }

        // Dispose channel - disposing automatically closes the channel
        _channel.Dispose();
        _logger?.LogDebug("Disposed channel for queue {QueueName}", _queueName);
      } catch (Exception ex) {
        _logger?.LogError(ex, "Error disposing subscription for queue {QueueName}", _queueName);
        // Ignore errors during async disposal
      }
    }, CancellationToken.None);
  }
}
