using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Whizbang.Core.Transports;

/// <summary>
/// One <see cref="ISubscription"/> handle over the same logical subscription opened in several
/// TransportNamespaces (transport traffic classes, topology arc phase 8). Pause/resume/dispose
/// fan out to every namespace, so a consumer that mirrors an inbox across broker namespaces
/// still has exactly one lifecycle to manage.
/// </summary>
/// <remarks>
/// Constructed only by <see cref="NamespaceRoutingTransport"/>, and only when there IS something
/// to mirror — a single-namespace subscribe returns the underlying subscription unwrapped so the
/// no-op guarantee holds down to object identity.
/// </remarks>
/// <docs>messaging/transports/transports#transport-namespaces</docs>
/// <tests>tests/Whizbang.Core.Tests/Transports/NamespaceRoutingTransportTests.cs:CompositeSubscription_PauseResumeDispose_FanOutToEveryNamespaceAsync</tests>
internal sealed class CompositeSubscription : ISubscription {
  private readonly IReadOnlyList<ISubscription> _subscriptions;
  private bool _disposed;

  internal CompositeSubscription(IReadOnlyList<ISubscription> subscriptions) {
    _subscriptions = subscriptions;
    foreach (var subscription in subscriptions) {
      subscription.OnDisconnected += _forwardDisconnected;
    }
  }

  /// <inheritdoc />
  public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

  /// <summary>
  /// True only while EVERY namespace's subscription is active — a mirror that dropped means
  /// this service is no longer receiving that class's traffic, which must not read as healthy.
  /// </summary>
  public bool IsActive => _subscriptions.All(static s => s.IsActive);

  /// <inheritdoc />
  public async Task PauseAsync() {
    foreach (var subscription in _subscriptions) {
      await subscription.PauseAsync().ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public async Task ResumeAsync() {
    foreach (var subscription in _subscriptions) {
      await subscription.ResumeAsync().ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (_disposed) {
      return;
    }

    _disposed = true;
    foreach (var subscription in _subscriptions) {
      subscription.OnDisconnected -= _forwardDisconnected;
      subscription.Dispose();
    }
  }

  private void _forwardDisconnected(object? sender, SubscriptionDisconnectedEventArgs args) =>
    OnDisconnected?.Invoke(this, args);
}
