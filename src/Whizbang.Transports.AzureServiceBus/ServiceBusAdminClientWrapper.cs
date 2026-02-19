using Azure.Messaging.ServiceBus.Administration;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Wrapper for ServiceBusAdministrationClient that implements IServiceBusAdminClient.
/// Provides a testable abstraction over the Azure SDK's sealed classes.
/// </summary>
/// <docs>transports/azure-service-bus#admin-client</docs>
public sealed class ServiceBusAdminClientWrapper : IServiceBusAdminClient {
  private readonly ServiceBusAdministrationClient _adminClient;

  /// <summary>
  /// Initializes a new instance wrapping a ServiceBusAdministrationClient.
  /// </summary>
  /// <param name="adminClient">The underlying admin client</param>
  public ServiceBusAdminClientWrapper(ServiceBusAdministrationClient adminClient) {
    ArgumentNullException.ThrowIfNull(adminClient);
    _adminClient = adminClient;
  }

  /// <inheritdoc />
  public async Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default) {
    var response = await _adminClient.TopicExistsAsync(topicName, cancellationToken);
    return response.Value;
  }

  /// <inheritdoc />
  public async Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default) {
    await _adminClient.CreateTopicAsync(topicName, cancellationToken);
  }
}
