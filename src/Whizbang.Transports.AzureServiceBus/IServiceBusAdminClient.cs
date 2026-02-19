namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Interface abstraction for ServiceBusAdministrationClient to enable testing.
/// Wraps Azure SDK's ServiceBusAdministrationClient which uses sealed classes.
/// </summary>
/// <docs>transports/azure-service-bus#admin-client</docs>
public interface IServiceBusAdminClient {
  /// <summary>
  /// Checks if a topic exists in the Service Bus namespace.
  /// </summary>
  Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default);

  /// <summary>
  /// Creates a topic in the Service Bus namespace.
  /// </summary>
  Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default);
}
