using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Opens the connection for a NON-default TransportNamespace (transport traffic classes,
/// topology arc phase 8 — plan resolution 7). The default namespace keeps using the container's
/// ambient <see cref="IConnection"/>; the class namespaces come from here.
/// </summary>
/// <remarks>
/// <para>
/// A RabbitMQ TransportNamespace is a whole connection. A separate <b>vhost</b> on the same
/// broker is the recommended isolation — one AMQP URI per vhost (<c>amqp://host/bulk</c>) —
/// because it gives the traffic class its own exchange/queue namespace, its own permissions and
/// its own resource alarms without standing up a second cluster. A separate cluster works
/// identically; only the URI differs.
/// </para>
/// <para>
/// A seam because the default implementation blocks on a live connection handshake, which unit
/// tests must be able to replace without a broker. Registered with <c>TryAddSingleton</c>, so a
/// host or test can substitute its own.
/// </para>
/// </remarks>
/// <docs>messaging/transports/rabbitmq#transport-namespaces</docs>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQNamespaceRoutingRegistrationTests.cs</tests>
public interface IRabbitMQNamespaceConnectionFactory {
  /// <summary>Opens the connection for <paramref name="namespaceKey"/>.</summary>
  /// <param name="namespaceKey">The TransportNamespace key.</param>
  /// <param name="connectionString">That namespace's AMQP URI (vhost included).</param>
  /// <param name="options">The transport options (retry policy, channel ceiling).</param>
  /// <returns>A connected <see cref="IConnection"/>.</returns>
  IConnection CreateConnection(string namespaceKey, string connectionString, RabbitMQOptions options);
}

/// <summary>
/// Default <see cref="IRabbitMQNamespaceConnectionFactory"/>: opens each class namespace with
/// the SAME connection factory settings and retry policy as the default namespace — a
/// traffic-class namespace is a first-class broker connection, not a best-effort side channel.
/// </summary>
internal sealed class RabbitMQNamespaceConnectionFactory(ILogger<RabbitMQConnectionRetry>? retryLogger)
  : IRabbitMQNamespaceConnectionFactory {
  /// <inheritdoc />
  [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The connection is owned by the namespace-routing transport composition, which disposes every namespace transport.")]
  public IConnection CreateConnection(string namespaceKey, string connectionString, RabbitMQOptions options) {
    var factory = new ConnectionFactory {
      Uri = new Uri(connectionString),
      AutomaticRecoveryEnabled = true,
      NetworkRecoveryInterval = options.InitialRetryDelay,
      ConsumerDispatchConcurrency = 200
    };

    return new RabbitMQConnectionRetry(options, retryLogger)
      .CreateConnectionWithRetryAsync(factory).GetAwaiter().GetResult();
  }
}
