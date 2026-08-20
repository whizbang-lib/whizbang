using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Opens the broker clients for a NON-default TransportNamespace (transport traffic classes,
/// topology arc phase 8). The default namespace keeps using the container's ambient
/// <see cref="ServiceBusClient"/>; the class namespaces come from here.
/// </summary>
/// <remarks>
/// A seam for the same reason <see cref="IServiceBusAdminClient"/> is one: the default
/// implementation blocks on a live connectivity handshake, which unit tests must be able to
/// replace without a broker. Registered with <c>TryAddSingleton</c>, so a host or test can
/// substitute its own (managed identity, a shared client, a recording double) before calling
/// <c>AddAzureServiceBusTransport</c>.
/// </remarks>
/// <docs>messaging/transports/azure-service-bus#transport-namespaces</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusNamespaceRoutingRegistrationTests.cs</tests>
public interface IServiceBusNamespaceClientFactory {
  /// <summary>Creates the data-plane client for <paramref name="namespaceKey"/>.</summary>
  /// <param name="namespaceKey">The TransportNamespace key.</param>
  /// <param name="connectionString">That namespace's connection string.</param>
  /// <param name="options">The transport options (retry policy, provisioning switch).</param>
  /// <returns>A connected client.</returns>
  ServiceBusClient CreateClient(string namespaceKey, string connectionString, AzureServiceBusOptions options);

  /// <summary>
  /// Creates the management-plane client for <paramref name="namespaceKey"/>, or null when
  /// <see cref="AzureServiceBusOptions.AutoProvisionInfrastructure"/> is off. Each namespace
  /// gets its OWN admin plane so subscribe-time provisioning and the manifest pass create the
  /// mirrored entities in the namespace that will consume them.
  /// </summary>
  /// <param name="namespaceKey">The TransportNamespace key.</param>
  /// <param name="connectionString">That namespace's connection string.</param>
  /// <param name="options">The transport options.</param>
  /// <returns>The admin client, or null when auto-provisioning is disabled.</returns>
  IServiceBusAdminClient? CreateAdminClient(string namespaceKey, string connectionString, AzureServiceBusOptions options);
}

/// <summary>
/// Default <see cref="IServiceBusNamespaceClientFactory"/>: opens each class namespace with the
/// SAME connection-retry policy as the default namespace — a traffic-class namespace is a
/// first-class broker connection, not a best-effort side channel.
/// </summary>
internal sealed class ServiceBusNamespaceClientFactory(ILogger<AzureServiceBusConnectionRetry>? retryLogger)
  : IServiceBusNamespaceClientFactory {
  /// <inheritdoc />
  public ServiceBusClient CreateClient(string namespaceKey, string connectionString, AzureServiceBusOptions options) =>
    new AzureServiceBusConnectionRetry(options, retryLogger)
      .CreateClientWithRetryAsync(connectionString).GetAwaiter().GetResult();

  /// <inheritdoc />
  public IServiceBusAdminClient? CreateAdminClient(
      string namespaceKey, string connectionString, AzureServiceBusOptions options) =>
    options.AutoProvisionInfrastructure
      ? new ServiceBusAdminClientWrapper(new ServiceBusAdministrationClient(connectionString))
      : null;
}
