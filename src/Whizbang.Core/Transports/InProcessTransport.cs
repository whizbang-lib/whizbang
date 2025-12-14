using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Capabilities_ReturnsExpectedFlagsAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_WithNoSubscribers_CompletesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_WithSingleSubscriber_InvokesHandlerAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_WithMultipleSubscribers_InvokesAllHandlersAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_ToDifferentTopics_OnlyInvokesMatchingSubscribersAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SubscribeAsync_ReturnsActiveSubscriptionAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SubscribeAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_InVariousStates_BehavesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_PauseAsync_SetsIsActiveToFalseAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_ResumeAsync_SetsIsActiveToTrueAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_Dispose_RemovesHandlerFromTransportAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_DisposeMultipleTimes_IsIdempotentAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SendAsync_WithResponder_ReturnsResponseEnvelopeAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SendAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SendAsync_WithTimeout_ThrowsTimeoutExceptionAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_ConcurrentPublishes_AllHandlersInvokedAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SubscribeAsync_ConcurrentSubscriptions_AllRegisteredAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SubscribeAndDispose_Concurrent_ThreadSafeAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_HandlerThrowsException_ContinuesWithOtherHandlersAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SendAsync_WithCancellationDuringPublish_ExecutesFinallyBlockAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_WithRoutingKey_DeliversToCorrectDestinationAsync</tests>
/// In-process transport implementation for local message passing.
/// Messages are delivered synchronously within the same process.
/// Useful for testing and single-process scenarios.
/// </summary>
public class InProcessTransport : ITransport {
  private readonly ConcurrentDictionary<string, List<(Func<IMessageEnvelope, CancellationToken, Task> handler, InProcessSubscription subscription)>> _subscriptions = new();
  private bool _isInitialized;

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_WithSingleSubscriber_InvokesHandlerAsync</tests>
  public bool IsInitialized => _isInitialized;

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_WithSingleSubscriber_InvokesHandlerAsync</tests>
  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();

    // In-process transport is always ready immediately
    // Idempotent - safe to call multiple times
    _isInitialized = true;
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Capabilities_ReturnsExpectedFlagsAsync</tests>
  public TransportCapabilities Capabilities =>
    TransportCapabilities.RequestResponse |
    TransportCapabilities.PublishSubscribe |
    TransportCapabilities.Ordered |
    TransportCapabilities.Reliable;

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_WithNoSubscribers_CompletesSuccessfullyAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_WithSingleSubscriber_InvokesHandlerAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_WithMultipleSubscribers_InvokesAllHandlersAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_ToDifferentTopics_OnlyInvokesMatchingSubscribersAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_ConcurrentPublishes_AllHandlersInvokedAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_HandlerThrowsException_ContinuesWithOtherHandlersAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:PublishAsync_WithRoutingKey_DeliversToCorrectDestinationAsync</tests>
  public async Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) {
    cancellationToken.ThrowIfCancellationRequested();

    if (_subscriptions.TryGetValue(destination.Address, out var subscriptionHandlers)) {
      foreach (var (handler, subscription) in subscriptionHandlers.ToArray()) {
        // Only invoke handler if subscription is active and not disposed
        if (subscription.IsActive) {
          await handler(envelope, cancellationToken);
        }
      }
    }
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SubscribeAsync_ReturnsActiveSubscriptionAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SubscribeAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SubscribeAsync_ConcurrentSubscriptions_AllRegisteredAsync</tests>
  public Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) {
    cancellationToken.ThrowIfCancellationRequested();

    var subscription = new InProcessSubscription(
      onDispose: () => {
        if (_subscriptions.TryGetValue(destination.Address, out var subscriptionHandlers)) {
          lock (subscriptionHandlers) {
            subscriptionHandlers.RemoveAll(sh => sh.handler == handler);
          }
        }
      }
    );

    var subscriptionHandlers = _subscriptions.GetOrAdd(destination.Address, _ => []);
    lock (subscriptionHandlers) {
      subscriptionHandlers.Add((handler, subscription));
    }

    return Task.FromResult<ISubscription>(subscription);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SendAsync_WithResponder_ReturnsResponseEnvelopeAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SendAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SendAsync_WithTimeout_ThrowsTimeoutExceptionAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SendAsync_WithCancellationDuringPublish_ExecutesFinallyBlockAsync</tests>
  public async Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
    IMessageEnvelope requestEnvelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) where TRequest : notnull where TResponse : notnull {
    cancellationToken.ThrowIfCancellationRequested();

    // Create response destination based on request MessageId
    var responseDestination = new TransportDestination($"response-{requestEnvelope.MessageId.Value}");
    var tcs = new TaskCompletionSource<IMessageEnvelope>();

    // Subscribe to response destination before publishing request
    var responseSubscription = await SubscribeAsync(
      handler: (envelope, ct) => {
        tcs.TrySetResult(envelope);
        return Task.CompletedTask;
      },
      destination: responseDestination,
      cancellationToken: cancellationToken
    );

    try {
      // Publish the request
      await PublishAsync(requestEnvelope, destination, cancellationToken);

      // Wait for response (with cancellation support)
      using (cancellationToken.Register(() => tcs.TrySetCanceled())) {
        return await tcs.Task;
      }
    } finally {
      // DEFENSIVE: Always cleanup response subscription (success, exception, or cancellation)
      responseSubscription.Dispose();
    }
  }

  /// <summary>
  /// In-process subscription implementation.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_InVariousStates_BehavesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_PauseAsync_SetsIsActiveToFalseAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_ResumeAsync_SetsIsActiveToTrueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_Dispose_RemovesHandlerFromTransportAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_DisposeMultipleTimes_IsIdempotentAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SubscribeAndDispose_Concurrent_ThreadSafeAsync</tests>
  private class InProcessSubscription(Action onDispose) : ISubscription {
    private readonly Action _onDispose = onDispose;
    private bool _isDisposed;

    /// <summary>
    /// 
    /// </summary>
    /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_InVariousStates_BehavesCorrectlyAsync</tests>
    /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_PauseAsync_SetsIsActiveToFalseAsync</tests>
    /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_ResumeAsync_SetsIsActiveToTrueAsync</tests>
    /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_Dispose_RemovesHandlerFromTransportAsync</tests>
    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// 
    /// </summary>
    /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_InVariousStates_BehavesCorrectlyAsync</tests>
    /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_PauseAsync_SetsIsActiveToFalseAsync</tests>
    public Task PauseAsync() {
      IsActive = false;
      return Task.CompletedTask;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_InVariousStates_BehavesCorrectlyAsync</tests>
    /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_ResumeAsync_SetsIsActiveToTrueAsync</tests>
    public Task ResumeAsync() {
      IsActive = true;
      return Task.CompletedTask;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_InVariousStates_BehavesCorrectlyAsync</tests>
    /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_Dispose_RemovesHandlerFromTransportAsync</tests>
    /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:Subscription_DisposeMultipleTimes_IsIdempotentAsync</tests>
    /// <tests>tests/Whizbang.Transports.Tests/InProcessTransportTests.cs:SubscribeAndDispose_Concurrent_ThreadSafeAsync</tests>
    public void Dispose() {
      if (!_isDisposed) {
        IsActive = false;
        _onDispose();
        _isDisposed = true;
      }
    }
  }
}
