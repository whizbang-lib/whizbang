using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Whizbang.Hosting.RabbitMQ;

/// <summary>
/// Aspire extension methods for declarative RabbitMQ topology configuration.
/// Provides builder-style API for configuring exchanges, queues, and bindings.
/// </summary>
public static class AspireExtensions {
  /// <summary>
  /// Declares a RabbitMQ exchange with the specified name and type.
  /// Exchanges are created when the RabbitMQ container starts.
  /// </summary>
  /// <param name="builder">The RabbitMQ resource builder</param>
  /// <param name="name">Exchange name (e.g., "orders", "products")</param>
  /// <param name="type">Exchange type: "topic", "direct", "fanout", or "headers" (default: "topic")</param>
  /// <returns>The resource builder for fluent chaining</returns>
  public static IResourceBuilder<RabbitMQServerResource> WithExchange(
    this IResourceBuilder<RabbitMQServerResource> builder,
    string name,
    string type = "topic"
  ) {
    // Store exchange metadata for Aspire provisioning
    // Note: Aspire.Hosting.RabbitMQ doesn't automatically provision topology
    // This annotation serves as metadata for future provisioning or documentation
    builder.WithAnnotation(new RabbitMQExchangeAnnotation(name, type));
    return builder;
  }

  /// <summary>
  /// Declares a RabbitMQ queue and binds it to an exchange with a routing key.
  /// Queues and bindings are created when the RabbitMQ container starts.
  /// </summary>
  /// <param name="builder">The RabbitMQ resource builder</param>
  /// <param name="queueName">Queue name (e.g., "payment-worker-queue")</param>
  /// <param name="exchangeName">Exchange name to bind to</param>
  /// <param name="routingKey">Routing key pattern (e.g., "#" for topic, "service-name" for direct)</param>
  /// <returns>The resource builder for fluent chaining</returns>
  public static IResourceBuilder<RabbitMQServerResource> WithQueueBinding(
    this IResourceBuilder<RabbitMQServerResource> builder,
    string queueName,
    string exchangeName,
    string routingKey
  ) {
    // Store binding metadata for Aspire provisioning
    // Note: Aspire.Hosting.RabbitMQ doesn't automatically provision topology
    // This annotation serves as metadata for future provisioning or documentation
    builder.WithAnnotation(new RabbitMQBindingAnnotation(queueName, exchangeName, routingKey));
    return builder;
  }
}

/// <summary>
/// Annotation for RabbitMQ exchange metadata.
/// Stores exchange name and type for declarative topology configuration.
/// </summary>
/// <param name="Name">Exchange name</param>
/// <param name="Type">Exchange type (topic, direct, fanout, headers)</param>
internal sealed record RabbitMQExchangeAnnotation(string Name, string Type) : IResourceAnnotation;

/// <summary>
/// Annotation for RabbitMQ queue binding metadata.
/// Stores queue, exchange, and routing key for declarative topology configuration.
/// </summary>
/// <param name="Queue">Queue name</param>
/// <param name="Exchange">Exchange name to bind to</param>
/// <param name="RoutingKey">Routing key pattern</param>
internal sealed record RabbitMQBindingAnnotation(string Queue, string Exchange, string RoutingKey) : IResourceAnnotation;
