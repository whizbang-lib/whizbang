using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
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

  /// <inheritdoc />
  public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

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

    // Subscribe to channel closed event to detect disconnections
    _channel.ChannelShutdownAsync += _onChannelShutdownAsync;
  }

  /// <summary>
  /// Handles channel shutdown events and fires OnDisconnected.
  /// </summary>
  private Task _onChannelShutdownAsync(object sender, ShutdownEventArgs args) {
    // Don't fire event if we're being disposed (application-initiated)
    if (_disposed) {
      return Task.CompletedTask;
    }

    var isApplicationInitiated = args.Initiator == ShutdownInitiator.Application;
    var reason = args.ReplyText ?? $"Code: {args.ReplyCode}";

    _logger?.LogWarning(
      "RabbitMQ channel shutdown for queue {QueueName}: {Reason} (Initiator: {Initiator})",
      _queueName,
      reason,
      args.Initiator
    );

    // Mark as inactive
    _isActive = false;

    // Fire disconnection event for non-application-initiated shutdowns
    // This allows immediate reconnection attempts
    if (!isApplicationInitiated) {
      OnDisconnected?.Invoke(this, new SubscriptionDisconnectedEventArgs {
        Reason = reason,
        IsApplicationInitiated = false,
        Exception = args.Exception
      });
    }

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public bool IsActive => _isActive && !_disposed;

  /// <inheritdoc />
  public Task PauseAsync() {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (!_isActive) {
      if (_logger?.IsEnabled(LogLevel.Debug) == true) {
        var queueName = _queueName;
        _logger.LogDebug("Subscription for queue {QueueName} already paused, skipping", queueName);
      }
      return Task.CompletedTask;
    }

    _isActive = false;
    if (_logger?.IsEnabled(LogLevel.Information) == true) {
      var queueName = _queueName;
      _logger.LogInformation("Paused subscription for queue {QueueName}", queueName);
    }

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task ResumeAsync() {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (_isActive) {
      if (_logger?.IsEnabled(LogLevel.Debug) == true) {
        var queueName = _queueName;
        _logger.LogDebug("Subscription for queue {QueueName} already active, skipping", queueName);
      }
      return Task.CompletedTask;
    }

    _isActive = true;
    if (_logger?.IsEnabled(LogLevel.Information) == true) {
      var queueName = _queueName;
      _logger.LogInformation("Resumed subscription for queue {QueueName}", queueName);
    }

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (_disposed) {
      return;
    }

    _disposed = true;

    // Unsubscribe from channel events
    _channel.ChannelShutdownAsync -= _onChannelShutdownAsync;

    // Fire-and-forget disposal to avoid blocking on RabbitMQ channel cleanup
    // Channel cleanup can block if the broker is slow to respond
    _ = Task.Run(async () => {
      try {
        // Cancel consumer explicitly if consumer tag is available
        // Use noWait: true to avoid waiting for server confirmation
        if (_consumerTag != null) {
          await _channel.BasicCancelAsync(_consumerTag, noWait: true);
          if (_logger?.IsEnabled(LogLevel.Debug) == true) {
            var consumerTag = _consumerTag;
            var queueName = _queueName;
            _logger.LogDebug("Cancelled consumer {ConsumerTag} for queue {QueueName}", consumerTag, queueName);
          }
        }

        // Dispose channel - disposing automatically closes the channel
        _channel.Dispose();
        if (_logger?.IsEnabled(LogLevel.Debug) == true) {
          var queueName = _queueName;
          _logger.LogDebug("Disposed channel for queue {QueueName}", queueName);
        }
      } catch (Exception ex) {
        _logger?.LogError(ex, "Error disposing subscription for queue {QueueName}", _queueName);
        // Ignore errors during async disposal
      }
    }, CancellationToken.None);
  }
}
