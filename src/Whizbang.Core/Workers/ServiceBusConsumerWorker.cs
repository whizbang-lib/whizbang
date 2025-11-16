using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background service that subscribes to messages from Azure Service Bus and dispatches them to local receptors.
/// </summary>
public class ServiceBusConsumerWorker : BackgroundService {
  private readonly ITransport _transport;
  private readonly IDispatcher _dispatcher;
  private readonly IInbox _inbox;
  private readonly ILogger<ServiceBusConsumerWorker> _logger;
  private readonly List<ISubscription> _subscriptions = new();
  private readonly ServiceBusConsumerOptions _options;

  public ServiceBusConsumerWorker(
    ITransport transport,
    IDispatcher dispatcher,
    IInbox inbox,
    ILogger<ServiceBusConsumerWorker> logger,
    ServiceBusConsumerOptions? options = null
  ) {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _options = options ?? new ServiceBusConsumerOptions();
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    _logger.LogInformation("ServiceBusConsumerWorker starting...");

    try {
      // Subscribe to configured topics
      foreach (var topicConfig in _options.Subscriptions) {
        var destination = new TransportDestination(
          topicConfig.TopicName,
          topicConfig.SubscriptionName
        );

        var subscription = await _transport.SubscribeAsync(
          async (envelope, ct) => await HandleMessageAsync(envelope, ct),
          destination,
          stoppingToken
        );

        _subscriptions.Add(subscription);

        _logger.LogInformation(
          "Subscribed to topic {TopicName} with subscription {SubscriptionName}",
          topicConfig.TopicName,
          topicConfig.SubscriptionName
        );
      }

      // Keep the worker running while subscriptions are active
      await Task.Delay(Timeout.Infinite, stoppingToken);
    } catch (OperationCanceledException) {
      _logger.LogInformation("ServiceBusConsumerWorker is stopping...");
    } catch (Exception ex) {
      _logger.LogError(ex, "Fatal error in ServiceBusConsumerWorker");
      throw;
    }
  }

  private async Task HandleMessageAsync(IMessageEnvelope envelope, CancellationToken ct) {
    try {
      // Check inbox for deduplication
      if (await _inbox.HasProcessedAsync(envelope.MessageId, ct)) {
        _logger.LogInformation(
          "Message {MessageId} already processed, skipping",
          envelope.MessageId
        );
        return;
      }

      _logger.LogInformation(
        "Processing message {MessageId} from Service Bus",
        envelope.MessageId
      );

      // Dispatch to local receptors
      // Note: We're using PublishAsync here because events from Service Bus
      // should be published to all local event handlers
      await _dispatcher.PublishAsync(envelope.GetPayload());

      // Mark as processed in inbox
      await _inbox.MarkProcessedAsync(envelope.MessageId, "ServiceBusConsumerWorker", ct);

      _logger.LogInformation(
        "Successfully processed message {MessageId}",
        envelope.MessageId
      );
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Error processing message {MessageId}",
        envelope.MessageId
      );
      throw; // Let the transport handle retry/dead-letter
    }
  }

  public override async Task StopAsync(CancellationToken cancellationToken) {
    _logger.LogInformation("ServiceBusConsumerWorker stopping...");

    // Dispose all subscriptions
    foreach (var subscription in _subscriptions) {
      subscription.Dispose();
    }

    await base.StopAsync(cancellationToken);
  }
}

/// <summary>
/// Configuration options for ServiceBusConsumerWorker.
/// </summary>
public class ServiceBusConsumerOptions {
  /// <summary>
  /// List of topic subscriptions to consume messages from.
  /// </summary>
  public List<TopicSubscription> Subscriptions { get; set; } = new();
}

/// <summary>
/// Configuration for a single topic subscription.
/// </summary>
/// <param name="TopicName">The Service Bus topic name</param>
/// <param name="SubscriptionName">The subscription name for this consumer</param>
public record TopicSubscription(string TopicName, string SubscriptionName);
