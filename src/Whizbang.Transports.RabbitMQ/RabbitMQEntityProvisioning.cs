using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// The ONE code path for declaring RabbitMQ subscription infrastructure (topology arc
/// phase 5): both the transport's subscribe-time setup and the provisioner's manifest-driven
/// DARK provisioning delegate here, so exchange/queue arguments and binding shapes can never
/// drift between the two (a queue re-declared with different arguments fails with
/// PRECONDITION_FAILED).
/// </summary>
/// <docs>fundamentals/dispatcher/routing#topology-manifest</docs>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerManifestTests.cs:ProvisionManifest_QueueArguments_MatchTransportConventionAsync</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Infrastructure provisioning - startup overhead not critical")]
internal static class RabbitMQEntityProvisioning {
  /// <summary>
  /// Declares the full subscription infrastructure on <paramref name="channel"/>: the topic
  /// exchange, the dead-letter exchange + queue (when enabled), the subscription queue with
  /// the transport's argument set, and one binding per routing pattern. Every declare is
  /// broker-idempotent when the arguments match.
  /// </summary>
  internal static async Task DeclareSubscriptionInfrastructureAsync(
    IChannel channel,
    RabbitMQOptions options,
    string exchangeName,
    string queueName,
    IReadOnlyList<string> routingPatterns,
    ILogger? logger,
    CancellationToken cancellationToken,
    bool controlClass = false
  ) {
    await channel.ExchangeDeclareAsync(
      exchange: exchangeName,
      type: "topic",
      durable: true,
      autoDelete: false,
      arguments: null,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );

    if (options.AutoDeclareDeadLetterExchange) {
      await DeclareDeadLetterExchangeAsync(channel, exchangeName, queueName, cancellationToken);
    }

    var queueArgs = new Dictionary<string, object?>();
    if (options.AutoDeclareDeadLetterExchange) {
      queueArgs["x-dead-letter-exchange"] = $"{exchangeName}.dlx";
    }
    // Control class (topology arc phase 9): the plain-queue analogue of a sessionless Service Bus
    // subscription. RabbitMQ's ordering primitive pins a queue to ONE consumer at a time; control
    // consumers need no ordering, so pinning them only reintroduces the head-of-line queueing the
    // class exists to avoid. The dead-letter exchange above is untouched — plain is about ordering,
    // never about giving up the broker's own poison valve.
    if (options.EnableSingleActiveConsumer && !controlClass) {
      queueArgs["x-single-active-consumer"] = true;
    }

    await channel.QueueDeclareAsync(
      queue: queueName,
      durable: true,
      exclusive: false,
      autoDelete: false,
      arguments: queueArgs,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );

    foreach (var pattern in routingPatterns) {
      if (logger?.IsEnabled(LogLevel.Debug) == true) {
        logger.LogDebug(
          "Binding queue {QueueName} to exchange {ExchangeName} with routing pattern {Pattern}",
          queueName,
          exchangeName,
          pattern
        );
      }

      await channel.QueueBindAsync(
        queue: queueName,
        exchange: exchangeName,
        routingKey: pattern,
        arguments: null,
        noWait: false,
        cancellationToken: cancellationToken
      );
    }
  }

  /// <summary>
  /// Declares dead letter exchange and queue for a given exchange/queue pair.
  /// </summary>
  internal static async Task DeclareDeadLetterExchangeAsync(
    IChannel channel,
    string exchangeName,
    string queueName,
    CancellationToken cancellationToken
  ) {
    var dlxName = $"{exchangeName}.dlx";
    var dlqName = $"{queueName}.dlq";

    await channel.ExchangeDeclareAsync(
      exchange: dlxName,
      type: "fanout",
      durable: true,
      autoDelete: false,
      arguments: null,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );

    await channel.QueueDeclareAsync(
      queue: dlqName,
      durable: true,
      exclusive: false,
      autoDelete: false,
      arguments: null,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );

    await channel.QueueBindAsync(
      queue: dlqName,
      exchange: dlxName,
      routingKey: "",
      arguments: null,
      noWait: false,
      cancellationToken: cancellationToken
    );
  }
}
