using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Transports;

/// <summary>
/// Composes one broker client per <see cref="TransportNamespaces">TransportNamespace</see> into
/// a single <see cref="ITransport"/> (transport traffic classes, topology arc phase 8 / #424
/// increment 2). Publish routes on the namespace key the publish strategy stamped onto
/// destination metadata; the consume side subscribes its normal entity set in <c>default</c>
/// PLUS the same entity set in every non-default namespace an actively consumed/handled type
/// resolves to.
/// </summary>
/// <remarks>
/// <para>
/// This type is broker-agnostic on purpose: a TransportNamespace is a whole broker namespace
/// (an Azure Service Bus namespace, a RabbitMQ connection/vhost), so "one client per class" is
/// naturally "one transport instance per class". Composing at the <see cref="ITransport"/> seam
/// keeps every transport's internals — sender caches, admin clients, session acceptors, the
/// ops-rate projection — scoped to exactly one namespace, which is also why the per-namespace
/// governors cannot double-count each other.
/// </para>
/// <para>
/// <b>Naming invariant.</b> A namespace decides WHICH broker, never WHAT the entity is called:
/// destinations are forwarded verbatim, so an inbox entity has the same name in every namespace
/// and the topology manifest stays namespace-independent.
/// </para>
/// <para>
/// <b>Single-namespace no-op.</b> Hosts with only the default namespace never construct this
/// type at all (the transport registrations short-circuit to today's single-client path), and
/// even when it IS constructed a subscribe with nothing to mirror returns the default
/// transport's own subscription rather than a wrapper.
/// </para>
/// <para>
/// <b>Ownership.</b> The namespace transports are constructed by the registration factory that
/// builds this composition, so this type disposes ALL of them (default included).
/// </para>
/// </remarks>
/// <docs>messaging/transports/transports#transport-namespaces</docs>
/// <tests>tests/Whizbang.Core.Tests/Transports/NamespaceRoutingTransportTests.cs</tests>
public sealed class NamespaceRoutingTransport : ITransport, ITransportWithRecovery, IAsyncDisposable {
  private readonly ITransport _default;
  private readonly IReadOnlyDictionary<string, ITransport> _byNamespace;
  private readonly Func<IReadOnlyList<string>>? _activeConsumeNamespaceKeys;
  private bool _disposed;

  /// <summary>
  /// Creates the composition.
  /// </summary>
  /// <param name="defaultTransport">
  /// The <see cref="TransportNamespaces.DefaultKey"/> transport — the connection every host has
  /// today. Unstamped traffic, and any key with no configured namespace, rides this one.
  /// </param>
  /// <param name="namespaceTransports">
  /// The NON-default namespaces, keyed by TransportNamespace key (ordinal). Must not contain
  /// the default key: that transport is <paramref name="defaultTransport"/>, and carrying it
  /// twice would open a second client onto the same namespace.
  /// </param>
  /// <param name="activeConsumeNamespaceKeys">
  /// Projects the non-default namespaces this service actually consumes from — typically
  /// <c>TransportNamespaceResolver.ResolveConsumeNamespaceKeys</c> over the receptor registry's
  /// handled-message enumeration. Deferred (not a snapshot) because the registry is only
  /// complete after every module initializer has run. Null or empty means publish-only
  /// namespaces: nothing is mirrored, so a class nobody handles costs zero broker entities.
  /// </param>
  /// <exception cref="ArgumentException"><paramref name="namespaceTransports"/> contains the default key.</exception>
  public NamespaceRoutingTransport(
      ITransport defaultTransport,
      IReadOnlyDictionary<string, ITransport> namespaceTransports,
      Func<IReadOnlyList<string>>? activeConsumeNamespaceKeys = null) {
    ArgumentNullException.ThrowIfNull(defaultTransport);
    ArgumentNullException.ThrowIfNull(namespaceTransports);

    if (namespaceTransports.ContainsKey(TransportNamespaces.DefaultKey)) {
      throw new ArgumentException(
        $"The namespace transport map must not contain the reserved '{TransportNamespaces.DefaultKey}' key — "
        + "the default namespace is the first constructor argument. Carrying it in both places opens a second "
        + "client onto the same broker namespace, which is exactly the cost this composition exists to avoid.",
        nameof(namespaceTransports));
    }

    _default = defaultTransport;
    _byNamespace = namespaceTransports;
    _activeConsumeNamespaceKeys = activeConsumeNamespaceKeys;
  }

  /// <summary>
  /// Every configured TransportNamespace key: <see cref="TransportNamespaces.DefaultKey"/>
  /// first, then the non-default keys in ordinal order.
  /// </summary>
  public IReadOnlyList<string> NamespaceKeys =>
    [TransportNamespaces.DefaultKey, .. _byNamespace.Keys.OrderBy(static k => k, StringComparer.Ordinal)];

  /// <summary>
  /// The transports this composition owns, default first — the enumeration per-namespace
  /// health and provisioning surfaces walk.
  /// </summary>
  public IReadOnlyList<ITransport> Transports =>
    [_default, .. NamespaceKeys.Skip(1).Select(k => _byNamespace[k])];

  /// <summary>
  /// Resolves the transport for <paramref name="namespaceKey"/>, falling back to the default
  /// namespace for the default key and for any key with no configured connection. The fallback
  /// is deliberate graceful degradation: a routing binding on a host that was never given a
  /// second connection changes nothing but a metadata stamp — it never drops a message.
  /// </summary>
  /// <param name="namespaceKey">The TransportNamespace key.</param>
  /// <returns>The transport for that namespace; never null.</returns>
  public ITransport Resolve(string namespaceKey) =>
    namespaceKey is not null && _byNamespace.TryGetValue(namespaceKey, out var transport) ? transport : _default;

  /// <inheritdoc />
  public bool IsInitialized => _default.IsInitialized && _byNamespace.Values.All(static t => t.IsInitialized);

  /// <summary>
  /// The default namespace's capabilities. Every namespace runs the same broker product under
  /// the same options, and the publish strategy reads capabilities before any namespace key
  /// exists, so the default's answer IS the composition's.
  /// </summary>
  public TransportCapabilities Capabilities => _default.Capabilities;

  /// <inheritdoc cref="ITransport.MaxMessageSizeBytes" />
  public long? MaxMessageSizeBytes => _default.MaxMessageSizeBytes;

  /// <inheritdoc />
  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    await _default.InitializeAsync(cancellationToken).ConfigureAwait(false);
    foreach (var transport in _byNamespace.Values) {
      await transport.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public async ValueTask<bool> CheckConnectivityAsync(CancellationToken cancellationToken = default) {
    if (!await _default.CheckConnectivityAsync(cancellationToken).ConfigureAwait(false)) {
      return false;
    }

    foreach (var transport in _byNamespace.Values) {
      if (!await transport.CheckConnectivityAsync(cancellationToken).ConfigureAwait(false)) {
        return false;
      }
    }

    return true;
  }

  /// <inheritdoc />
  public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      ReadOnlyMemory<byte>? preSerializedBytes = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(destination);
    return _resolveFor(destination)
      .PublishAsync(envelope, destination, envelopeType, preSerializedBytes, cancellationToken);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(
      IReadOnlyList<BulkPublishItem> items,
      TransportDestination destination,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(destination);
    return _resolveFor(destination).PublishBatchAsync(items, destination, cancellationToken);
  }

  /// <inheritdoc />
  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default)
      where TRequest : notnull
      where TResponse : notnull {
    ArgumentNullException.ThrowIfNull(destination);
    return _resolveFor(destination).SendAsync<TRequest, TResponse>(requestEnvelope, destination, cancellationToken);
  }

  /// <inheritdoc />
  public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination,
      TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default) =>
    _mirrorSubscribeAsync(
      transport => transport.SubscribeBatchAsync(batchHandler, destination, batchOptions, cancellationToken));

  /// <inheritdoc />
  public Task<ISubscription> SubscribeToDeadLetterAsync(
      Func<TransportMessage, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default) =>
    _mirrorSubscribeAsync(
      transport => transport.SubscribeToDeadLetterAsync(handler, destination, cancellationToken));

  /// <inheritdoc />
  public void SetRecoveryHandler(Func<CancellationToken, Task>? onRecovered) {
    foreach (var transport in Transports) {
      if (transport is ITransportWithRecovery recoverable) {
        recoverable.SetRecoveryHandler(onRecovered);
      }
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    _disposed = true;
    foreach (var transport in Transports) {
      switch (transport) {
        case IAsyncDisposable asyncDisposable:
          await asyncDisposable.DisposeAsync().ConfigureAwait(false);
          break;
        case IDisposable disposable:
          disposable.Dispose();
          break;
        default:
          break;
      }
    }
  }

  private ITransport _resolveFor(TransportDestination destination) =>
    Resolve(TransportNamespaces.FromMetadata(destination.Metadata));

  /// <summary>
  /// Opens the subscription on the default namespace and mirrors it — same entity name, same
  /// handler — into every ACTIVE non-default namespace. With nothing to mirror the default
  /// transport's own subscription is returned unwrapped, so the single-namespace path is
  /// indistinguishable from calling the transport directly.
  /// </summary>
  private async Task<ISubscription> _mirrorSubscribeAsync(Func<ITransport, Task<ISubscription>> subscribe) {
    var subscription = await subscribe(_default).ConfigureAwait(false);

    var mirrors = _activeMirrorTransports();
    if (mirrors.Count == 0) {
      return subscription;
    }

    var subscriptions = new List<ISubscription>(mirrors.Count + 1) { subscription };
    foreach (var transport in mirrors) {
      subscriptions.Add(await subscribe(transport).ConfigureAwait(false));
    }

    return new CompositeSubscription(subscriptions);
  }

  private IReadOnlyList<ITransport> _activeMirrorTransports() {
    if (_byNamespace.Count == 0 || _activeConsumeNamespaceKeys is null) {
      return [];
    }

    // Keys the resolver reports but this host has no connection for are ignored, not an error:
    // the routing bindings and the connection map are configured independently, and a class
    // whose namespace was never provisioned must degrade to default rather than fail startup.
    return [.. _activeConsumeNamespaceKeys()
      .Where(key => !TransportNamespaces.IsDefault(key) && _byNamespace.ContainsKey(key))
      .Distinct(StringComparer.Ordinal)
      .OrderBy(static key => key, StringComparer.Ordinal)
      .Select(key => _byNamespace[key])];
  }
}
