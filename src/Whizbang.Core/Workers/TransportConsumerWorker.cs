using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance (not critical for worker startup/shutdown)

namespace Whizbang.Core.Workers;

/// <summary>
/// Generic background service that consumes messages from any ITransport implementation.
/// Subscribes to configured destinations with built-in resilience (retry with exponential backoff).
/// Uses both IReceptorInvoker (compile-time business receptors) and ILifecycleInvoker (runtime test receptors).
/// </summary>
/// <remarks>
/// <para>
/// Resilience features:
/// <list type="bullet">
/// <item>Exponential backoff retry for failed subscriptions</item>
/// <item>Per-destination state tracking</item>
/// <item>Connection recovery handling via <see cref="ITransportWithRecovery"/></item>
/// <item>Health monitoring for failed subscriptions</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>components/workers/transport-consumer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerTests.cs</tests>
public class TransportConsumerWorker : BackgroundService {
  private readonly ITransport _transport;
  private readonly TransportConsumerOptions _options;
  private readonly SubscriptionResilienceOptions _resilienceOptions;
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly OrderedStreamProcessor _orderedProcessor;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer;
  private readonly ILifecycleInvoker? _lifecycleInvoker;
  private readonly ILogger<TransportConsumerWorker> _logger;

  private readonly Dictionary<TransportDestination, SubscriptionState> _states = [];
  private CancellationTokenSource? _linkedCts;

  /// <summary>
  /// Initializes a new instance of TransportConsumerWorker.
  /// </summary>
  /// <param name="transport">The transport to consume messages from</param>
  /// <param name="options">Configuration options specifying destinations</param>
  /// <param name="resilienceOptions">Resilience options for subscription retry behavior</param>
  /// <param name="scopeFactory">Service scope factory for creating scoped services</param>
  /// <param name="jsonOptions">JSON serialization options</param>
  /// <param name="orderedProcessor">Ordered stream processor for message ordering</param>
  /// <param name="lifecycleMessageDeserializer">Optional lifecycle message deserializer for deserializing messages</param>
  /// <param name="lifecycleInvoker">Optional lifecycle invoker for runtime test/lifecycle receptors</param>
  /// <param name="logger">Logger instance</param>
  /// <remarks>
  /// <para>
  /// <strong>IReceptorInvoker is scoped:</strong> The receptor invoker is resolved from the per-message scope
  /// rather than being injected as a constructor parameter. This follows industry patterns (MediatR, MassTransit)
  /// where handlers are scoped and resolved from the message processing scope.
  /// </para>
  /// </remarks>
  public TransportConsumerWorker(
    ITransport transport,
    TransportConsumerOptions options,
    SubscriptionResilienceOptions resilienceOptions,
    IServiceScopeFactory scopeFactory,
    JsonSerializerOptions jsonOptions,
    OrderedStreamProcessor orderedProcessor,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer,
    ILifecycleInvoker? lifecycleInvoker,
    ILogger<TransportConsumerWorker> logger
  ) {
    ArgumentNullException.ThrowIfNull(transport);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(resilienceOptions);
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(jsonOptions);
    ArgumentNullException.ThrowIfNull(orderedProcessor);
    ArgumentNullException.ThrowIfNull(logger);

    _transport = transport;
    _options = options;
    _resilienceOptions = resilienceOptions;
    _scopeFactory = scopeFactory;
    _jsonOptions = jsonOptions;
    _orderedProcessor = orderedProcessor;
    _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
    _lifecycleInvoker = lifecycleInvoker;
    _logger = logger;

    // Initialize state for each destination
    foreach (var destination in _options.Destinations) {
      _states[destination] = new SubscriptionState(destination);
    }

    // Register recovery handler if transport supports it
    if (_transport is ITransportWithRecovery recoveryTransport) {
      recoveryTransport.SetRecoveryHandler(_onConnectionRecoveredAsync);
    }
  }

  /// <summary>
  /// Gets the current subscription states for health monitoring.
  /// </summary>
  public IReadOnlyDictionary<TransportDestination, SubscriptionState> SubscriptionStates => _states;

  /// <summary>
  /// Executes the worker, creating subscriptions for all configured destinations.
  /// </summary>
  /// <param name="stoppingToken">Token to signal shutdown</param>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (_logger.IsEnabled(LogLevel.Information)) {
      var destinationCount = _options.Destinations.Count;
      _logger.LogInformation("TransportConsumerWorker starting with {DestinationCount} destinations", destinationCount);
    }

    // Log all destinations we're going to subscribe to
    foreach (var destination in _options.Destinations) {
      if (_logger.IsEnabled(LogLevel.Information)) {
        var address = destination.Address;
        var routingKey = destination.RoutingKey ?? "#";
        _logger.LogInformation(
          "  → Destination: {Address} (routing key: {RoutingKey})",
          address,
          routingKey
        );
      }
    }

    _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

    // Wait for transport readiness if readiness check is configured
    using (var scope = _scopeFactory.CreateScope()) {
      var readinessCheck = scope.ServiceProvider.GetService<ITransportReadinessCheck>();
      if (readinessCheck != null) {
        _logger.LogInformation("Waiting for transport readiness");
        var isReady = await readinessCheck.IsReadyAsync(stoppingToken);
        if (!isReady) {
          _logger.LogWarning("Transport readiness check returned false");
          return;
        }
        _logger.LogInformation("Transport is ready");
      }

      // Provision infrastructure for owned domains before creating subscriptions
      var provisioner = scope.ServiceProvider.GetService<IInfrastructureProvisioner>();
      var routingOptions = scope.ServiceProvider.GetService<IOptions<RoutingOptions>>()?.Value;
      if (provisioner != null && routingOptions?.OwnedDomains.Count > 0) {
        if (_logger.IsEnabled(LogLevel.Information)) {
          var ownedDomainsCount = routingOptions.OwnedDomains.Count;
          _logger.LogInformation(
            "Provisioning infrastructure for {Count} owned domains",
            ownedDomainsCount);
        }

        await provisioner.ProvisionOwnedDomainsAsync(routingOptions.OwnedDomains, stoppingToken);

        _logger.LogInformation("Infrastructure provisioning completed");
      }
    }

    // Subscribe to all destinations with retry
    await _subscribeToAllDestinationsAsync(stoppingToken);

    if (_logger.IsEnabled(LogLevel.Information)) {
      var healthyCount = _states.Values.Count(s => s.Status == SubscriptionStatus.Healthy);
      var failedCount = _states.Values.Count(s => s.Status == SubscriptionStatus.Failed);

      _logger.LogInformation(
        "TransportConsumerWorker started with {HealthyCount} healthy, {FailedCount} failed subscriptions",
        healthyCount,
        failedCount
      );
    }

    // Start health monitor in background
    _ = _monitorSubscriptionHealthAsync(_linkedCts.Token);

    // Keep running until cancellation is requested
    try {
      await Task.Delay(Timeout.Infinite, stoppingToken);
    } catch (OperationCanceledException) {
      _logger.LogInformation("TransportConsumerWorker cancellation requested");
    }
  }

  /// <summary>
  /// Subscribes to all destinations with retry logic.
  /// </summary>
  private async Task _subscribeToAllDestinationsAsync(CancellationToken cancellationToken) {
    var tasks = _states.Values.Select(state =>
      _subscribeWithRetryAsync(state, cancellationToken)
    );

    if (_resilienceOptions.AllowPartialSubscriptions) {
      // Allow partial failures - wait for all tasks
      await Task.WhenAll(tasks);
    } else {
      // All must succeed - throw on first failure
      foreach (var task in tasks) {
        await task;
        var completedState = _states.Values.FirstOrDefault(s => s.Status == SubscriptionStatus.Failed);
        if (completedState != null) {
          throw new InvalidOperationException(
            $"Subscription to {completedState.Destination.Address} failed and AllowPartialSubscriptions=false"
          );
        }
      }
    }
  }

  /// <summary>
  /// Subscribes to a single destination with retry logic.
  /// </summary>
  private async Task _subscribeWithRetryAsync(SubscriptionState state, CancellationToken cancellationToken) {
    if (_logger.IsEnabled(LogLevel.Information)) {
      var address = state.Destination.Address;
      var routingKey = state.Destination.RoutingKey;
      _logger.LogInformation(
        "Creating subscription for destination: {Address}, routing key: {RoutingKey}",
        address,
        routingKey
      );
    }

    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      _transport,
      state.Destination,
      async (envelope, envelopeType, ct) => await _handleMessageAsync(envelope, envelopeType, ct),
      state,
      _resilienceOptions,
      _logger,
      cancellationToken
    );
  }

  /// <summary>
  /// Handles connection recovery by re-establishing all subscriptions.
  /// </summary>
  private async Task _onConnectionRecoveredAsync(CancellationToken cancellationToken) {
    _logger.LogInformation("Connection recovered, re-establishing subscriptions...");

    // Reset all states to Pending
    foreach (var state in _states.Values) {
      state.Subscription?.Dispose();
      state.Subscription = null;
      state.Status = SubscriptionStatus.Pending;
      state.ResetAttempts();
    }

    // Re-subscribe to all destinations
    await _subscribeToAllDestinationsAsync(cancellationToken);

    _logger.LogInformation("Subscription re-establishment completed");
  }

  /// <summary>
  /// Background task that monitors subscription health and attempts recovery.
  /// </summary>
  private async Task _monitorSubscriptionHealthAsync(CancellationToken cancellationToken) {
    while (!cancellationToken.IsCancellationRequested) {
      try {
        await Task.Delay(_resilienceOptions.HealthCheckInterval, cancellationToken);

        var failedStates = _states.Values
          .Where(s => s.Status == SubscriptionStatus.Failed)
          .ToList();

        if (failedStates.Count > 0) {
          if (_logger.IsEnabled(LogLevel.Information)) {
            var count = failedStates.Count;
            _logger.LogInformation(
              "Health monitor: attempting to recover {Count} failed subscriptions",
              count
            );
          }

          foreach (var state in failedStates) {
            // Reset and retry in background
            state.Status = SubscriptionStatus.Pending;
            state.ResetAttempts();
            _ = _subscribeWithRetryAsync(state, cancellationToken);
          }
        }
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        _logger.LogError(ex, "Error in subscription health monitor");
      }
    }
  }

  /// <summary>
  /// Handles a received message using the inbox/work-coordinator pattern.
  /// Messages are stored in the inbox via process_work_batch for atomic deduplication.
  /// Perspectives are processed asynchronously by PerspectiveWorker.
  /// </summary>
  private async Task _handleMessageAsync(
    IMessageEnvelope envelope,
    string? envelopeType,
    CancellationToken cancellationToken
  ) {
    try {
      // Create scope to resolve scoped services (IWorkCoordinatorStrategy, IReceptorInvoker)
      await using var scope = _scopeFactory.CreateAsyncScope();
      var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();

      // Resolve IReceptorInvoker from scope (scoped service following MediatR/MassTransit pattern)
      var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();

      if (_logger.IsEnabled(LogLevel.Debug)) {
        var messageId = envelope.MessageId;
        _logger.LogDebug(
          "Processing message {MessageId} from transport",
          messageId
        );
      }

      // 1. Serialize envelope to InboxMessage
      var newInboxMessage = _serializeToNewInboxMessage(envelope, envelopeType, scope.ServiceProvider);

      // 2. Queue for atomic deduplication via process_work_batch
      strategy.QueueInboxMessage(newInboxMessage);

      // 3. Flush - calls process_work_batch with atomic INSERT ... ON CONFLICT DO NOTHING
      var workBatch = await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

      // 4. Check if work was returned - empty means duplicate (already processed)
      var myWork = workBatch.InboxWork.Where(w => w.MessageId == envelope.MessageId.Value).ToList();

      if (myWork.Count == 0) {
        if (_logger.IsEnabled(LogLevel.Information)) {
          var messageId = envelope.MessageId;
          _logger.LogInformation(
            "Message {MessageId} already processed (duplicate), skipping",
            messageId
          );
        }
        return;
      }

      if (_logger.IsEnabled(LogLevel.Debug)) {
        var messageId = envelope.MessageId;
        var workCount = myWork.Count;
        _logger.LogDebug(
          "Message {MessageId} accepted for processing ({WorkCount} inbox work items)",
          messageId,
          workCount
        );
      }

      // 5. Invoke PreInbox lifecycle stages (ALL receptors registered at PreInbox stages)
      foreach (var work in myWork) {
        object? message = null;
        IMessageEnvelope? typedEnvelope = null;

        // Deserialize message if we have any invoker
        if (_lifecycleMessageDeserializer is not null && (receptorInvoker is not null || _lifecycleInvoker is not null)) {
          message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
          // Reconstruct envelope with deserialized payload to preserve security context
          typedEnvelope = work.Envelope.ReconstructWithPayload(message);
        }

        if (typedEnvelope is not null) {
          var lifecycleContext = new LifecycleExecutionContext {
            CurrentStage = LifecycleStage.PreInboxAsync,
            EventId = null,
            StreamId = null,
            LastProcessedEventId = null,
            MessageSource = MessageSource.Inbox,
            AttemptNumber = null // Attempt info not tracked for inbox work
          };

          // Invoke compile-time business receptors via IReceptorInvoker
          if (receptorInvoker is not null) {
            await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PreInboxAsync, lifecycleContext, cancellationToken);
          }

          // Invoke runtime test/lifecycle receptors via ILifecycleInvoker
          if (_lifecycleInvoker is not null) {
            await _lifecycleInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PreInboxAsync, lifecycleContext, cancellationToken);
          }

          // PreInboxInline stage
          lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PreInboxInline };

          if (receptorInvoker is not null) {
            await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PreInboxInline, lifecycleContext, cancellationToken);
          }

          if (_lifecycleInvoker is not null) {
            await _lifecycleInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PreInboxInline, lifecycleContext, cancellationToken);
          }
        }
      }

      // 6. Process using OrderedStreamProcessor (maintains stream ordering)
      // Perspectives are created automatically by process_work_batch and processed by PerspectiveWorker
      await _orderedProcessor.ProcessInboxWorkAsync(
        myWork,
        processor: async (work) => {
          // Deserialize event from work item
          var @event = _deserializeEvent(work);

          // Mark as EventStored - perspectives will be processed via PerspectiveWorker
          if (@event is IEvent) {
            return MessageProcessingStatus.EventStored;
          }

          // Non-event messages - just mark as stored
          return MessageProcessingStatus.EventStored;
        },
        completionHandler: (msgId, status) => {
          strategy.QueueInboxCompletion(msgId, status);
          if (_logger.IsEnabled(LogLevel.Debug)) {
            _logger.LogDebug("Queued completion for {MessageId} with status {Status}", msgId, status);
          }
        },
        failureHandler: (msgId, status, error) => {
          strategy.QueueInboxFailure(msgId, status, error);
          _logger.LogError("Queued failure for {MessageId}: {Error}", msgId, error);
        },
        cancellationToken
      );

      // 7. Invoke PostInbox lifecycle stages (ALL receptors registered at PostInbox stages)
      // This is where DEFAULT receptors (without [FireAt]) fire for the distributed receive path
      foreach (var work in myWork) {
        object? message = null;
        IMessageEnvelope? typedEnvelope = null;

        // Deserialize message if we have any invoker
        if (_lifecycleMessageDeserializer is not null && (receptorInvoker is not null || _lifecycleInvoker is not null)) {
          message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
          // Reconstruct envelope with deserialized payload to preserve security context
          typedEnvelope = work.Envelope.ReconstructWithPayload(message);
        }

        if (typedEnvelope is not null) {
          var lifecycleContext = new LifecycleExecutionContext {
            CurrentStage = LifecycleStage.PostInboxAsync,
            EventId = null,
            StreamId = null,
            LastProcessedEventId = null,
            MessageSource = MessageSource.Inbox,
            AttemptNumber = null // Attempt info not tracked for inbox work
          };

          // Invoke compile-time business receptors via IReceptorInvoker
          if (receptorInvoker is not null) {
            await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostInboxAsync, lifecycleContext, cancellationToken);
          }

          // Invoke runtime test/lifecycle receptors via ILifecycleInvoker
          if (_lifecycleInvoker is not null) {
            await _lifecycleInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostInboxAsync, lifecycleContext, cancellationToken);
          }

          // PostInboxInline stage
          lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostInboxInline };

          if (receptorInvoker is not null) {
            await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostInboxInline, lifecycleContext, cancellationToken);
          }

          if (_lifecycleInvoker is not null) {
            await _lifecycleInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostInboxInline, lifecycleContext, cancellationToken);
          }
        }
      }

      // 8. Report completions/failures back to database
      await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

      if (_logger.IsEnabled(LogLevel.Debug)) {
        var messageId = envelope.MessageId;
        _logger.LogDebug("Successfully processed message {MessageId}", messageId);
      }
    } catch (Exception ex) {
      _logger.LogError(ex, "Error processing message {MessageId}", envelope.MessageId);
      throw; // Let the transport handle retry/dead-letter
    }
  }

  /// <summary>
  /// Creates InboxMessage for work coordinator pattern.
  /// Handles envelopes from transport which may be strongly-typed or JsonElement-typed.
  /// </summary>
  private InboxMessage _serializeToNewInboxMessage(
    IMessageEnvelope envelope,
    string? envelopeTypeFromTransport,
    IServiceProvider scopeServiceProvider
  ) {
    if (string.IsNullOrWhiteSpace(envelopeTypeFromTransport)) {
      throw new InvalidOperationException(
        $"EnvelopeType is required from transport but was null/empty. MessageId: {envelope.MessageId}");
    }

    // Extract message type from envelope type string
    var messageTypeName = _extractMessageTypeFromEnvelopeType(envelopeTypeFromTransport);

    // Get payload to check its type
    var payload = envelope.Payload;
    var payloadType = payload?.GetType() ?? typeof(object);

    // Check if envelope/payload is already in JsonElement form
    IMessageEnvelope<JsonElement> jsonEnvelope;
    if (envelope is IMessageEnvelope<JsonElement> alreadyJsonEnvelope) {
      jsonEnvelope = alreadyJsonEnvelope;
    } else if (payloadType == typeof(JsonElement)) {
      throw new InvalidOperationException(
        $"Envelope has JsonElement payload but envelope type is {envelope.GetType().Name}. MessageId: {envelope.MessageId}");
    } else {
      // Strongly-typed envelope - serialize it
      var serializer = scopeServiceProvider.GetService<IEnvelopeSerializer>();
      if (serializer == null) {
        throw new InvalidOperationException("IEnvelopeSerializer is required but not registered");
      }

      // Call generic SerializeEnvelope method via reflection
      var genericMethod = typeof(IEnvelopeSerializer).GetMethod(nameof(IEnvelopeSerializer.SerializeEnvelope));
      var boundMethod = genericMethod!.MakeGenericMethod(payloadType);
      var serialized = (SerializedEnvelope)boundMethod.Invoke(serializer, new object[] { envelope })!;
      jsonEnvelope = serialized.JsonEnvelope;
    }

    // Determine if message is an event using IEventTypeProvider
    // This is more reliable than "payload is IEvent" when payload is JsonElement
    var isEvent = false;
    var eventTypeProvider = scopeServiceProvider.GetService<IEventTypeProvider>();
    if (eventTypeProvider != null) {
      var eventTypes = eventTypeProvider.GetEventTypes();
      isEvent = EventTypeMatchingHelper.IsEventType(messageTypeName, eventTypes);
    } else {
      // Fallback to runtime check if provider not available
      isEvent = payload is IEvent;
    }

    // Extract simple type name
    var lastDotIndex = messageTypeName.LastIndexOf('.');
    var simpleTypeName = lastDotIndex >= 0
      ? messageTypeName.Substring(lastDotIndex + 1).Split(',')[0].Trim()
      : messageTypeName.Split(',')[0].Trim();
    var handlerName = simpleTypeName + "Handler";

    var streamId = _extractStreamId(envelope);

    return new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = handlerName,
      Envelope = jsonEnvelope,
      EnvelopeType = envelopeTypeFromTransport,
      StreamId = streamId,
      IsEvent = isEvent,
      MessageType = messageTypeName
    };
  }

  /// <summary>
  /// Extracts message type from envelope type name.
  /// </summary>
  private static string _extractMessageTypeFromEnvelopeType(string envelopeTypeName) {
    var startIndex = envelopeTypeName.IndexOf("[[", StringComparison.Ordinal);
    var endIndex = envelopeTypeName.IndexOf("]]", StringComparison.Ordinal);

    if (startIndex == -1 || endIndex == -1 || startIndex >= endIndex) {
      throw new InvalidOperationException($"Invalid envelope type name format: '{envelopeTypeName}'");
    }

    var messageTypeName = envelopeTypeName.Substring(startIndex + 2, endIndex - startIndex - 2);

    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new InvalidOperationException($"Failed to extract message type from envelope type: '{envelopeTypeName}'");
    }

    return messageTypeName;
  }

  /// <summary>
  /// Deserializes event payload from InboxWork.
  /// </summary>
  private object? _deserializeEvent(InboxWork work) {
    try {
      var jsonElement = work.Envelope.Payload;
      var jsonTypeInfo = Serialization.JsonContextRegistry.GetTypeInfoByName(work.MessageType, _jsonOptions);
      if (jsonTypeInfo == null) {
        _logger.LogError("Could not resolve JsonTypeInfo for type {MessageType} for message {MessageId}",
          work.MessageType, work.MessageId);
        return null;
      }

      return JsonSerializer.Deserialize(jsonElement, jsonTypeInfo);
    } catch (Exception ex) {
      _logger.LogError(ex, "Failed to deserialize event for message {MessageId}", work.MessageId);
      return null;
    }
  }

  /// <summary>
  /// Extracts stream_id from envelope for stream-based ordering.
  /// Uses [StreamId] attribute value stored in metadata as "AggregateId" for backward compatibility.
  /// </summary>
  private static Guid _extractStreamId(IMessageEnvelope envelope) {
    // Note: Metadata key is "AggregateId" for backward compatibility with existing envelopes
    var firstHop = envelope.Hops.FirstOrDefault();
    if (firstHop?.Metadata != null && firstHop.Metadata.TryGetValue("AggregateId", out var streamIdElem) &&
        streamIdElem.ValueKind == JsonValueKind.String) {
      var streamIdStr = streamIdElem.GetString();
      if (streamIdStr != null && Guid.TryParse(streamIdStr, out var parsedStreamId)) {
        return parsedStreamId;
      }
    }

    return envelope.MessageId.Value;
  }

  /// <summary>
  /// Pauses all active subscriptions.
  /// Messages will not be processed until resumed.
  /// </summary>
  public async Task PauseAllSubscriptionsAsync() {
    _logger.LogInformation("Pausing all subscriptions");

    foreach (var state in _states.Values) {
      if (state.Subscription != null) {
        await state.Subscription.PauseAsync();
      }
    }

    _logger.LogInformation("All subscriptions paused");
  }

  /// <summary>
  /// Resumes all paused subscriptions.
  /// Message processing will continue.
  /// </summary>
  public async Task ResumeAllSubscriptionsAsync() {
    _logger.LogInformation("Resuming all subscriptions");

    foreach (var state in _states.Values) {
      if (state.Subscription != null) {
        await state.Subscription.ResumeAsync();
      }
    }

    _logger.LogInformation("All subscriptions resumed");
  }

  /// <summary>
  /// Stops the worker and disposes all subscriptions.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  public override async Task StopAsync(CancellationToken cancellationToken) {
    _logger.LogInformation("Stopping TransportConsumerWorker");

    _linkedCts?.Cancel();

    // Dispose all subscriptions
    foreach (var state in _states.Values) {
      state.Subscription?.Dispose();
    }

    _states.Clear();

    _logger.LogInformation("TransportConsumerWorker stopped");

    await base.StopAsync(cancellationToken);
  }
}
