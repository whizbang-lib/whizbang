using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// In-process transport implementation for local message passing.
/// Messages are delivered synchronously within the same process.
/// Useful for testing and single-process scenarios.
/// </summary>
public class InProcessTransport : ITransport {
  private readonly ConcurrentDictionary<string, List<Func<IMessageEnvelope, CancellationToken, Task>>> _subscriptions = new();
  private readonly ConcurrentDictionary<string, TaskCompletionSource<IMessageEnvelope>> _pendingRequests = new();

  /// <inheritdoc />
  public TransportCapabilities Capabilities =>
    TransportCapabilities.RequestResponse |
    TransportCapabilities.PublishSubscribe |
    TransportCapabilities.Ordered |
    TransportCapabilities.Reliable;

  /// <inheritdoc />
  public async Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) {
    cancellationToken.ThrowIfCancellationRequested();

    if (_subscriptions.TryGetValue(destination.Address, out var handlers)) {
      foreach (var handler in handlers.ToArray()) {
        await handler(envelope, cancellationToken);
      }
    }
  }

  /// <inheritdoc />
  public Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) {
    cancellationToken.ThrowIfCancellationRequested();

    var handlers = _subscriptions.GetOrAdd(destination.Address, _ => new List<Func<IMessageEnvelope, CancellationToken, Task>>());
    lock (handlers) {
      handlers.Add(handler);
    }

    var subscription = new InProcessSubscription(
      onDispose: () => {
        if (_subscriptions.TryGetValue(destination.Address, out var h)) {
          lock (h) {
            h.Remove(handler);
          }
        }
      }
    );

    return Task.FromResult<ISubscription>(subscription);
  }

  /// <inheritdoc />
  public async Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
    IMessageEnvelope requestEnvelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) where TRequest : notnull where TResponse : notnull {
    cancellationToken.ThrowIfCancellationRequested();

    var requestId = Guid.NewGuid().ToString();
    var tcs = new TaskCompletionSource<IMessageEnvelope>();
    _pendingRequests[requestId] = tcs;

    try {
      // Publish the request
      await PublishAsync(requestEnvelope, destination, cancellationToken);

      // Wait for response (with cancellation support)
      using (cancellationToken.Register(() => tcs.TrySetCanceled())) {
        return await tcs.Task;
      }
    } finally {
      // DEFENSIVE: Always cleanup pending request (success, exception, or cancellation)
      // Coverage: May appear uncovered due to coverage tool artifacts with finally blocks
      _pendingRequests.TryRemove(requestId, out _);
    }
  }

  /// <summary>
  /// In-process subscription implementation.
  /// </summary>
  private class InProcessSubscription : ISubscription {
    private readonly Action _onDispose;
    private bool _isDisposed;

    public InProcessSubscription(Action onDispose) {
      _onDispose = onDispose;
    }

    public bool IsActive { get; private set; } = true;

    public Task PauseAsync() {
      IsActive = false;
      return Task.CompletedTask;
    }

    public Task ResumeAsync() {
      IsActive = true;
      return Task.CompletedTask;
    }

    public void Dispose() {
      if (!_isDisposed) {
        IsActive = false;
        _onDispose();
        _isDisposed = true;
      }
    }
  }
}
