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
  private readonly ILogger? _logger;
  private bool _isActive = true;
  private bool _disposed;

  /// <summary>
  /// Initializes a new instance of RabbitMQSubscription.
  /// </summary>
  /// <param name="channel">RabbitMQ channel used by this consumer</param>
  /// <param name="queueName">Queue name for this subscription</param>
  /// <param name="logger">Optional logger instance</param>
  public RabbitMQSubscription(
    IChannel channel,
    string queueName,
    ILogger? logger = null
  ) {
    ArgumentNullException.ThrowIfNull(channel);
    ArgumentNullException.ThrowIfNull(queueName);

    _channel = channel;
    _queueName = queueName;
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

    try {
      // Close channel (this will cancel all consumers on this channel)
      _channel.CloseAsync().GetAwaiter().GetResult();
      _logger?.LogInformation("Closed channel for queue {QueueName}", _queueName);

      // Dispose channel (it was created specifically for this subscription)
      _channel.Dispose();
      _logger?.LogDebug("Disposed channel for queue {QueueName}", _queueName);
    } catch (Exception ex) {
      _logger?.LogError(ex, "Error disposing subscription for queue {QueueName}", _queueName);
      // Don't rethrow in Dispose
    }
  }
}
