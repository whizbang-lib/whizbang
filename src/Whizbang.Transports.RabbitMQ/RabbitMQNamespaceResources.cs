using System.Diagnostics.CodeAnalysis;
using RabbitMQ.Client;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// The per-TransportNamespace connections and channel pools for the NON-default namespaces
/// (transport traffic classes, topology arc phase 8). One connection per namespace, SHARED by
/// the transport and the infrastructure provisioner — they are two views of the same broker, so
/// giving each its own connection would double the fleet's connection count for no benefit.
/// </summary>
/// <remarks>
/// Lazily built: a host with no class namespaces never opens anything, which is what keeps the
/// single-namespace guarantee true down to the connection count. The DI container owns this
/// singleton, so it disposes the connections and pools at shutdown; the namespace transports
/// deliberately do not (they never owned them, exactly like the default transport and the
/// ambient <see cref="IConnection"/>).
/// </remarks>
/// <docs>messaging/transports/rabbitmq#transport-namespaces</docs>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQNamespaceRoutingRegistrationTests.cs:AddRabbitMQTransport_MultiNamespaceMap_OpensOneConnectionPerNamespaceAsync</tests>
internal sealed class RabbitMQNamespaceResources : IDisposable {
  private readonly IRabbitMQNamespaceConnectionFactory _connectionFactory;
  private readonly RabbitMQOptions _options;
  private readonly Dictionary<string, (IConnection Connection, RabbitMQChannelPool Pool)> _byKey =
    new(StringComparer.Ordinal);
  private readonly Lock _lock = new();
  private bool _disposed;

  internal RabbitMQNamespaceResources(
      IRabbitMQNamespaceConnectionFactory connectionFactory,
      RabbitMQOptions options,
      IReadOnlyDictionary<string, string> namespaceConnectionStrings) {
    _connectionFactory = connectionFactory;
    _options = options;
    ConnectionStrings = namespaceConnectionStrings;
  }

  /// <summary>The effective NON-default namespace map (code map with configuration merged over it).</summary>
  internal IReadOnlyDictionary<string, string> ConnectionStrings { get; }

  /// <summary>The non-default TransportNamespace keys, in ordinal order.</summary>
  internal IReadOnlyList<string> Keys =>
    [.. ConnectionStrings.Keys.OrderBy(static key => key, StringComparer.Ordinal)];

  /// <summary>
  /// The connection and channel pool for <paramref name="namespaceKey"/>, opened on first use
  /// and shared from then on.
  /// </summary>
  /// <param name="namespaceKey">A non-default TransportNamespace key.</param>
  /// <returns>That namespace's connection and pool.</returns>
  [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The connection and pool are cached and disposed by this type's Dispose.")]
  internal (IConnection Connection, RabbitMQChannelPool Pool) Get(string namespaceKey) {
    lock (_lock) {
      ObjectDisposedException.ThrowIf(_disposed, this);

      if (_byKey.TryGetValue(namespaceKey, out var existing)) {
        return existing;
      }

      if (!ConnectionStrings.TryGetValue(namespaceKey, out var connectionString)) {
        throw new ArgumentException(
          $"No connection is configured for TransportNamespace '{namespaceKey}' (configured: "
          + $"{string.Join(", ", Keys)}). Add it to the AddRabbitMQTransport namespace map or to "
          + $"Whizbang:Transports:RabbitMQ:{TransportNamespaceConnectionStrings.NAMESPACES_SUBSECTION}.",
          nameof(namespaceKey));
      }

      var connection = _connectionFactory.CreateConnection(namespaceKey, connectionString, _options);
      var resources = (connection, new RabbitMQChannelPool(connection, _options.MaxChannels));
      _byKey[namespaceKey] = resources;
      return resources;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    lock (_lock) {
      if (_disposed) {
        return;
      }

      _disposed = true;
      foreach (var (connection, pool) in _byKey.Values) {
        pool.Dispose();
        connection.Dispose();
      }

      _byKey.Clear();
    }
  }
}
