namespace Whizbang.Core.Transports;

/// <summary>
/// Supported transport types for remote messaging.
/// </summary>
public enum TransportType {
  /// <summary>
  /// Apache Kafka / Azure Event Hubs transport
  /// </summary>
  Kafka = 0,

  /// <summary>
  /// Azure Service Bus transport
  /// </summary>
  ServiceBus = 1,

  /// <summary>
  /// RabbitMQ transport
  /// </summary>
  RabbitMQ = 2,

  /// <summary>
  /// Event Store transport
  /// </summary>
  EventStore = 3,

  /// <summary>
  /// In-process transport (for testing and local communication)
  /// </summary>
  InProcess = 4
}
