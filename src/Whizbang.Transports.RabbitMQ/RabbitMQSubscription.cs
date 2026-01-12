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
  private readonly string _consumerTag;
  private readonly ILogger? _logger;
  private bool _isActive = true;
  private bool _disposed;

  /// <summary>
  /// Initializes a new instance of RabbitMQSubscription.
  /// </summary>
  /// <param name="channel">RabbitMQ channel used by this consumer</param>
  /// <param name="consumerTag">Consumer tag for this subscription</param>
  /// <param name="logger">Optional logger instance</param>
  public RabbitMQSubscription(
    IChannel channel,
    string consumerTag,
    ILogger? logger = null
  ) {
    ArgumentNullException.ThrowIfNull(channel);
    ArgumentNullException.ThrowIfNull(consumerTag);

    _channel = channel;
    _consumerTag = consumerTag;
    _logger = logger;
  }

  /// <inheritdoc />
  public bool IsActive => _isActive && !_disposed;

  /// <inheritdoc />
  public Task PauseAsync() {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (!_isActive) {
      _logger?.LogDebug("Subscription {ConsumerTag} already paused, skipping", _consumerTag);
      return Task.CompletedTask;
    }

    _isActive = false;
    _logger?.LogInformation("Paused subscription {ConsumerTag}", _consumerTag);

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task ResumeAsync() {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (_isActive) {
      _logger?.LogDebug("Subscription {ConsumerTag} already active, skipping", _consumerTag);
      return Task.CompletedTask;
    }

    _isActive = true;
    _logger?.LogInformation("Resumed subscription {ConsumerTag}", _consumerTag);

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (_disposed) {
      return;
    }

    _disposed = true;

    try {
      // Cancel consumer on RabbitMQ server
      _channel.BasicCancelAsync(_consumerTag, noWait: false).GetAwaiter().GetResult();
      _logger?.LogInformation("Cancelled consumer {ConsumerTag}", _consumerTag);

      // Dispose channel (it was created specifically for this subscription)
      _channel.Dispose();
      _logger?.LogDebug("Disposed channel for subscription {ConsumerTag}", _consumerTag);
    } catch (Exception ex) {
      _logger?.LogError(ex, "Error disposing subscription {ConsumerTag}", _consumerTag);
      // Don't rethrow in Dispose
    }
  }
}
