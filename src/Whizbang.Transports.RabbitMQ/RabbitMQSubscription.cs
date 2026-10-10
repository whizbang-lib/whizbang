// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// RabbitMQ implementation of ISubscription.
/// Manages subscription lifecycle (pause, resume, dispose) for a RabbitMQ consumer.
/// </summary>
/// <docs>messaging/transports/rabbitmq</docs>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Subscription lifecycle management - simple logging where LoggerMessage overhead isn't justified")]
public sealed class RabbitMQSubscription : ISubscription {
  private readonly IChannel _channel;
  private readonly string _queueName;
  private readonly string? _consumerTag;
  // Always present: a subscription built without a logger discards through NullLogger, exactly as the
  // null-conditional calls it replaces did, and the fire-and-forget dispose then has no branch on it.
  private readonly ILogger _logger;
  // Completed by the cleanup Dispose() starts, once the consumer is cancelled and the channel is
  // disposed (or the failure is logged). Continuations run asynchronously so a caller awaiting it
  // never runs inline on the cleanup task.
  private readonly TaskCompletionSource _disposalCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    _logger = logger ?? NullLogger.Instance;

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

    _logger.LogWarning(
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
      if (_logger.IsEnabled(LogLevel.Debug)) {
        var queueName = _queueName;
        _logger.LogDebug("Subscription for queue {QueueName} already paused, skipping", queueName);
      }
      return Task.CompletedTask;
    }

    _isActive = false;
    if (_logger.IsEnabled(LogLevel.Information)) {
      var queueName = _queueName;
      _logger.LogInformation("Paused subscription for queue {QueueName}", queueName);
    }

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task ResumeAsync() {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (_isActive) {
      if (_logger.IsEnabled(LogLevel.Debug)) {
        var queueName = _queueName;
        _logger.LogDebug("Subscription for queue {QueueName} already active, skipping", queueName);
      }
      return Task.CompletedTask;
    }

    _isActive = true;
    if (_logger.IsEnabled(LogLevel.Information)) {
      var queueName = _queueName;
      _logger.LogInformation("Resumed subscription for queue {QueueName}", queueName);
    }

    return Task.CompletedTask;
  }

  /// <summary>
  /// Completes when the cleanup started by <see cref="Dispose"/> has finished: the consumer has been
  /// cancelled on the broker and the channel disposed. It stays pending until the subscription is
  /// disposed. It never faults: a failure while cancelling or closing is logged, and the task then
  /// completes, because there is nothing left for the caller to retry.
  /// </summary>
  /// <remarks>
  /// <see cref="Dispose"/> returns without waiting so a slow broker cannot block the caller. Await this
  /// during graceful shutdown to know the broker connection resources are actually released.
  /// </remarks>
  /// <docs>messaging/transports/rabbitmq</docs>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Component.Tests/RabbitMQSubscriptionTests.cs:DisposalCompletion_BeforeDispose_IsPendingAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Component.Tests/RabbitMQSubscriptionTests.cs:DisposalCompletion_AfterDispose_CompletesOnceConsumerCancelledAndChannelDisposedAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Component.Tests/RabbitMQSubscriptionCoverageTests.cs:Dispose_WhenChannelDisposeThrows_LogsTheErrorInsteadOfLosingItAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Component.Tests/RabbitMQTransportTests.cs:Subscription_Dispose_CancelsConsumerAsync</tests>
  public Task DisposalCompletion => _disposalCompletion.Task;

  /// <inheritdoc />
  /// <remarks>
  /// Returns without waiting for the broker. Observe <see cref="DisposalCompletion"/> to learn when the
  /// consumer is cancelled and the channel disposed.
  /// </remarks>
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
          if (_logger.IsEnabled(LogLevel.Debug)) {
            var consumerTag = _consumerTag;
            var queueName = _queueName;
            _logger.LogDebug("Canceled consumer {ConsumerTag} for queue {QueueName}", consumerTag, queueName);
          }
        }

        // Dispose channel - disposing automatically closes the channel
        _channel.Dispose();
        if (_logger.IsEnabled(LogLevel.Debug)) {
          var queueName = _queueName;
          _logger.LogDebug("Disposed channel for queue {QueueName}", queueName);
        }
      } catch (Exception ex) {
        // Logged, not rethrown: nothing awaits this cleanup to retry it, so the log is how an operator
        // learns the channel may have leaked. DisposalCompletion still completes below.
        _logger.LogError(ex, "Error disposing subscription for queue {QueueName}", _queueName);
      } finally {
        _disposalCompletion.TrySetResult();
      }
    }, CancellationToken.None);
  }
}
