using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance (not critical for worker startup/shutdown)

namespace Whizbang.Core.Workers;

/// <summary>
/// Generic background service that consumes messages from any ITransport implementation.
/// Subscribes to configured destinations and dispatches received messages to IDispatcher.
/// </summary>
/// <docs>components/workers/transport-consumer</docs>
public class TransportConsumerWorker : BackgroundService {
  private readonly ITransport _transport;
  private readonly TransportConsumerOptions _options;
  private readonly IServiceProvider _serviceProvider;
  private readonly ILogger<TransportConsumerWorker> _logger;
  private readonly List<ISubscription> _subscriptions = new();

  /// <summary>
  /// Initializes a new instance of TransportConsumerWorker.
  /// </summary>
  /// <param name="transport">The transport to consume messages from</param>
  /// <param name="options">Configuration options specifying destinations</param>
  /// <param name="serviceProvider">Service provider for creating scoped dispatchers</param>
  /// <param name="logger">Logger instance</param>
  public TransportConsumerWorker(
    ITransport transport,
    TransportConsumerOptions options,
    IServiceProvider serviceProvider,
    ILogger<TransportConsumerWorker> logger
  ) {
    ArgumentNullException.ThrowIfNull(transport);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(serviceProvider);
    ArgumentNullException.ThrowIfNull(logger);

    _transport = transport;
    _options = options;
    _serviceProvider = serviceProvider;
    _logger = logger;
  }

  /// <summary>
  /// Executes the worker, creating subscriptions for all configured destinations.
  /// </summary>
  /// <param name="stoppingToken">Token to signal shutdown</param>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    _logger.LogInformation("TransportConsumerWorker starting");

    // Wait for transport readiness if readiness check is configured
    var readinessCheck = _serviceProvider.GetService<ITransportReadinessCheck>();
    if (readinessCheck != null) {
      _logger.LogInformation("Waiting for transport readiness");
      var isReady = await readinessCheck.IsReadyAsync(stoppingToken);
      if (!isReady) {
        _logger.LogWarning("Transport readiness check returned false");
        return;
      }
      _logger.LogInformation("Transport is ready");
    }

    // Subscribe to each destination
    foreach (var destination in _options.Destinations) {
      _logger.LogInformation(
        "Creating subscription for destination: {Address}, routing key: {RoutingKey}",
        destination.Address,
        destination.RoutingKey
      );

      var subscription = await _transport.SubscribeAsync(
        async (envelope, envelopeType, ct) => await _handleMessageAsync(envelope, envelopeType, ct),
        destination,
        stoppingToken
      );

      _subscriptions.Add(subscription);
    }

    _logger.LogInformation(
      "TransportConsumerWorker started with {Count} subscriptions",
      _subscriptions.Count
    );

    // Keep running until cancellation is requested
    try {
      await Task.Delay(Timeout.Infinite, stoppingToken);
    } catch (OperationCanceledException) {
      _logger.LogInformation("TransportConsumerWorker cancellation requested");
    }
  }

  /// <summary>
  /// Handles a received message by dispatching it to the dispatcher.
  /// </summary>
  private async Task _handleMessageAsync(
    IMessageEnvelope envelope,
    string? envelopeType,
    CancellationToken cancellationToken
  ) {
    _logger.LogDebug(
      "Handling message: {MessageId}, envelope type: {EnvelopeType}",
      envelope.MessageId,
      envelopeType
    );

    // Create a scope for the dispatcher to ensure proper DI lifetime management
    using var scope = _serviceProvider.CreateScope();
    var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

    // Dispatch the envelope payload to appropriate handlers
    // The dispatcher will route based on the message type inside the envelope
    await dispatcher.SendAsync(envelope.Payload);
  }

  /// <summary>
  /// Pauses all active subscriptions.
  /// Messages will not be processed until resumed.
  /// </summary>
  public async Task PauseAllSubscriptionsAsync() {
    _logger.LogInformation("Pausing all subscriptions");

    foreach (var subscription in _subscriptions) {
      await subscription.PauseAsync();
    }

    _logger.LogInformation("All subscriptions paused");
  }

  /// <summary>
  /// Resumes all paused subscriptions.
  /// Message processing will continue.
  /// </summary>
  public async Task ResumeAllSubscriptionsAsync() {
    _logger.LogInformation("Resuming all subscriptions");

    foreach (var subscription in _subscriptions) {
      await subscription.ResumeAsync();
    }

    _logger.LogInformation("All subscriptions resumed");
  }

  /// <summary>
  /// Stops the worker and disposes all subscriptions.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  public override async Task StopAsync(CancellationToken cancellationToken) {
    _logger.LogInformation("Stopping TransportConsumerWorker");

    // Dispose all subscriptions
    foreach (var subscription in _subscriptions) {
      subscription.Dispose();
    }

    _subscriptions.Clear();

    _logger.LogInformation("TransportConsumerWorker stopped");

    await base.StopAsync(cancellationToken);
  }
}
