using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Attributes;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Lenses;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.Transports;
using Whizbang.Core.Validation;

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance (not critical for worker startup/shutdown)

namespace Whizbang.Core.Workers;

/// <summary>
/// Generic background service that consumes messages from any ITransport implementation.
/// Subscribes to configured destinations with built-in resilience (retry with exponential backoff).
/// Uses IReceptorInvoker for unified lifecycle receptor invocation (compile-time and runtime).
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
/// <docs>messaging/transports/transport-consumer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerSecurityContextTests.cs</tests>
public partial class TransportConsumerWorker : BackgroundService {
  private readonly ITransport _transport;
  private readonly TransportConsumerOptions _options;
  private readonly SubscriptionResilienceOptions _resilienceOptions;
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly OrderedStreamProcessor _orderedProcessor;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer;
  private readonly TransportMetrics? _metrics;
  private readonly ILogger<TransportConsumerWorker> _logger;

  private readonly ConcurrentBag<Task> _detachedTasks = [];
  private readonly HashSet<string> _ownedDomains;
  private readonly string? _serviceName;
  private readonly SemaphoreSlim? _concurrencySemaphore;
  private readonly IInboxBatchStrategy? _inboxBatchStrategy;
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
  /// <param name="metrics">Optional transport metrics for instrumentation</param>
  /// <param name="logger">Logger instance</param>
  /// <remarks>
  /// <para>
  /// <strong>IReceptorInvoker is scoped:</strong> The receptor invoker is resolved from the per-message scope
  /// rather than being injected as a constructor parameter. This follows industry patterns (MediatR, MassTransit)
  /// where handlers are scoped and resolved from the message processing scope.
  /// </para>
  /// </remarks>
#pragma warning disable S107 // Constructor uses DI injection — many parameters are idiomatic
  public TransportConsumerWorker(
    ITransport transport,
    TransportConsumerOptions options,
    SubscriptionResilienceOptions resilienceOptions,
    IServiceScopeFactory scopeFactory,
    JsonSerializerOptions jsonOptions,
    OrderedStreamProcessor orderedProcessor,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer,
    TransportMetrics? metrics,
    ILogger<TransportConsumerWorker> logger,
    Microsoft.Extensions.Options.IOptions<Routing.RoutingOptions>? routingOptions = null,
    IServiceInstanceProvider? serviceInstanceProvider = null,
    MessageProcessingOptions? messageProcessingOptions = null,
    IInboxBatchStrategy? inboxBatchStrategy = null
  ) {
#pragma warning restore S107
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
    _metrics = metrics;
    _logger = logger;
    _ownedDomains = routingOptions?.Value?.OwnedDomains?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
    _serviceName = serviceInstanceProvider?.ServiceName;
    _inboxBatchStrategy = inboxBatchStrategy;

    var maxConcurrent = messageProcessingOptions?.MaxConcurrentMessages ?? 40;
    _concurrencySemaphore = maxConcurrent > 0 ? new SemaphoreSlim(maxConcurrent) : null;

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
    // S3267: Loop has side effects (logging/state mutation) — LINQ not appropriate
#pragma warning disable S3267
    foreach (var destination in _options.Destinations) {
      if (_logger.IsEnabled(LogLevel.Debug)) {
        var address = destination.Address;
        var routingKey = destination.RoutingKey ?? "#";
        _logger.LogDebug(
          "  → Destination: {Address} (routing key: {RoutingKey})",
          address,
          routingKey
        );
      }
    }
#pragma warning restore S3267

    _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

    // Wait for transport readiness if readiness check is configured
    using (var scope = _scopeFactory.CreateScope()) {
      var readinessCheck = scope.ServiceProvider.GetService<ITransportReadinessCheck>();
      if (readinessCheck != null) {
        _logger.LogDebug("Waiting for transport readiness");
        var isReady = await readinessCheck.IsReadyAsync(stoppingToken);
        if (!isReady) {
          _logger.LogWarning("Transport readiness check returned false");
          return;
        }
        _logger.LogDebug("Transport is ready");
      }

      // Provision infrastructure for owned domains before creating subscriptions
      var provisioner = scope.ServiceProvider.GetService<IInfrastructureProvisioner>();
      var routingOptions = scope.ServiceProvider.GetService<IOptions<RoutingOptions>>()?.Value;
      if (provisioner != null && routingOptions?.OwnedDomains.Count > 0) {
        if (_logger.IsEnabled(LogLevel.Debug)) {
          var ownedDomainsCount = routingOptions.OwnedDomains.Count;
          _logger.LogDebug(
            "Provisioning infrastructure for {Count} owned domains",
            ownedDomainsCount);
        }

        await provisioner.ProvisionOwnedDomainsAsync(routingOptions.OwnedDomains, stoppingToken);

        _logger.LogDebug("Infrastructure provisioning completed");
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
    } catch (OperationCanceledException ex) {
      _logger.LogInformation(ex, "TransportConsumerWorker cancellation requested");
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
    if (_logger.IsEnabled(LogLevel.Debug)) {
      var address = state.Destination.Address;
      var routingKey = state.Destination.RoutingKey;
      _logger.LogDebug(
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
    var receiveSw = Stopwatch.StartNew();
    var messageType = envelopeType is not null ? TypeNameFormatter.GetSimpleName(envelopeType) : "Unknown";
    var messageTypeTag = new KeyValuePair<string, object?>("message_type", messageType);
    _metrics?.InboxMessagesReceived.Add(1, messageTypeTag);

    // Discard self-echo: owned messages from THIS service arriving back via transport.
    // Two conditions must be true:
    // 1. Owned namespace — message type belongs to this service's owned domains
    // 2. Same service — the last hop's service name matches this service
    // Messages from OTHER services always pass through (different service name in hop).
    if (_ownedDomains.Count > 0 && _serviceName is not null && envelopeType is not null) {
      var isSelfEcho = _isSelfEcho(envelope);
      var ns = TypeNameFormatter.GetPayloadNamespace(envelopeType);
      var isOwned = _isOwnedNamespace(ns);
      if (isSelfEcho && isOwned) {
        LogSelfEchoDiscarded(_logger, messageType, _serviceName);
        _metrics?.InboxMessagesDeduplicated.Add(1, messageTypeTag);
        return;
      }

      // Owned commands from other services fall through to normal inbox processing.
      // The PostInbox filter in ReceptorInvoker allows owned commands to fire:
      //   isPostInbox && (isOwned ? isEvent : !isEvent) → skip owned EVENTS (self-echo),
      //   but fire owned COMMANDS (cross-service delivery).
      // This preserves full scope context (tenant, security) established by the inbox pipeline.
    }

    var inboxActivity = _startInboxActivity(envelope, messageType);

    // Global concurrency gate — limits total concurrent handlers across all subscriptions
    // to prevent connection pool exhaustion. Self-echo discards above do NOT consume a slot.
    if (_concurrencySemaphore is not null) {
      var semaphoreSw = Stopwatch.StartNew();
      await _concurrencySemaphore.WaitAsync(cancellationToken);
      semaphoreSw.Stop();
      _metrics?.InboxConcurrencyWaitDuration.Record(semaphoreSw.Elapsed.TotalMilliseconds, messageTypeTag);
      _metrics?.InboxConcurrentMessages.Add(1, messageTypeTag);
    }

    try {
      await using var scope = _scopeFactory.CreateAsyncScope();

      var securitySw = Stopwatch.StartNew();
      await SecurityContextHelper.EstablishFullContextAsync(envelope, scope.ServiceProvider, cancellationToken);
      securitySw.Stop();
      _metrics?.InboxSecurityContextDuration.Record(securitySw.Elapsed.TotalMilliseconds, messageTypeTag);

      var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
      var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug("Processing message {MessageId} from transport", envelope.MessageId);
      }

      _populateDeliveredAtTimestamp(envelope, envelopeType);

      // 1. Serialize and deduplicate — use batch strategy if available, otherwise per-message flush
      List<InboxWork> myWork;
      if (_inboxBatchStrategy is not null) {
        var newInboxMessage = _serializeToNewInboxMessage(envelope, envelopeType, scope.ServiceProvider);
        var workBatch = await _inboxBatchStrategy.EnqueueAndWaitAsync(newInboxMessage, cancellationToken);
        myWork = [.. workBatch.InboxWork.Where(w => w.MessageId == envelope.MessageId.Value)];
      } else {
        myWork = await _serializeAndDeduplicateAsync(envelope, envelopeType, strategy, scope.ServiceProvider, messageTypeTag, cancellationToken);
      }

      if (myWork.Count == 0) {
        _metrics?.InboxMessagesDeduplicated.Add(1, messageTypeTag);
        if (_logger.IsEnabled(LogLevel.Information)) {
          _logger.LogInformation("Message {MessageId} already processed (duplicate), skipping", envelope.MessageId);
        }
        return;
      }

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug("Message {MessageId} accepted for processing ({WorkCount} inbox work items)", envelope.MessageId, myWork.Count);
      }

      // 2. PreInbox lifecycle
      await _invokePreInboxLifecycleAsync(myWork, receptorInvoker, cancellationToken);

      // 3. Process via OrderedStreamProcessor
      var processingSw = Stopwatch.StartNew();
      await _processInboxWorkItemsAsync(myWork, strategy, cancellationToken);
      processingSw.Stop();
      _metrics?.InboxProcessingDuration.Record(processingSw.Elapsed.TotalMilliseconds, messageTypeTag);

      // 4. PostInbox lifecycle
      // The LifecycleStageTracker in ReceptorInvoker prevents double-fire if the
      // WorkCoordinatorPublisherWorker also picks up the same message before completion flush.
      await _invokePostInboxLifecycleAsync(myWork, receptorInvoker, scope.ServiceProvider, cancellationToken);

      // 5. Report completions/failures
      var completionSw = Stopwatch.StartNew();
      await strategy.FlushAsync(WorkBatchOptions.None, FlushMode.BestEffort, cancellationToken);
      completionSw.Stop();
      _metrics?.InboxCompletionDuration.Record(completionSw.Elapsed.TotalMilliseconds, messageTypeTag);

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug("Successfully processed message {MessageId}", envelope.MessageId);
      }

      _metrics?.InboxMessagesProcessed.Add(1, messageTypeTag);
      inboxActivity?.SetStatus(ActivityStatusCode.Ok);
    } catch (ObjectDisposedException) {
      LogMessageDroppedDuringShutdown(_logger, envelope.MessageId.Value);
      return;
#pragma warning disable S2139 // Log + rethrow is intentional: transport needs the exception for retry/dead-letter, but we need the log for observability
    } catch (Exception ex) {
      _metrics?.InboxMessagesFailed.Add(1, messageTypeTag);
      inboxActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
      inboxActivity?.SetTag("exception.type", ex.GetType().FullName);
      inboxActivity?.SetTag("exception.message", ex.Message);
      _logger.LogError(ex, "Error processing message {MessageId}", envelope.MessageId);
      throw;
    } finally {
#pragma warning restore S2139
      if (_concurrencySemaphore is not null) {
        _concurrencySemaphore.Release();
        _metrics?.InboxConcurrentMessages.Add(-1, messageTypeTag);
      }
      receiveSw.Stop();
      _metrics?.InboxReceiveDuration.Record(receiveSw.Elapsed.TotalMilliseconds, messageTypeTag);
      inboxActivity?.Dispose();
    }
  }

  private static Activity? _startInboxActivity(IMessageEnvelope envelope, string messageType) {
    var traceParent = envelope.Hops?
      .Where(h => h.Type == HopType.Current)
      .Select(h => h.TraceParent)
      .LastOrDefault(tp => tp is not null);

    if (traceParent is null || !ActivityContext.TryParse(traceParent, null, out var parentContext)) {
      return null;
    }

    var activity = WhizbangActivitySource.Transport.StartActivity(
      $"Inbox {messageType}", ActivityKind.Consumer, parentContext);
    activity?.SetTag("messaging.message_id", envelope.MessageId.ToString());
    activity?.SetTag("messaging.operation", "receive");
    activity?.SetTag("whizbang.hop_count", envelope.Hops?.Count ?? 0);
    return activity;
  }

  private async Task<List<InboxWork>> _serializeAndDeduplicateAsync(
    IMessageEnvelope envelope, string? envelopeType, IWorkCoordinatorStrategy strategy,
    IServiceProvider scopedProvider, KeyValuePair<string, object?> messageTypeTag,
    CancellationToken cancellationToken) {
    var newInboxMessage = _serializeToNewInboxMessage(envelope, envelopeType, scopedProvider);
    strategy.QueueInboxMessage(newInboxMessage);

    var dedupSw = Stopwatch.StartNew();
    var workBatch = await strategy.FlushAsync(WorkBatchOptions.None, ct: cancellationToken);
    dedupSw.Stop();
    _metrics?.InboxDedupDuration.Record(dedupSw.Elapsed.TotalMilliseconds, messageTypeTag);

    return [.. workBatch.InboxWork.Where(w => w.MessageId == envelope.MessageId.Value)];
  }

  private async Task _invokePreInboxLifecycleAsync(
    List<InboxWork> myWork, IReceptorInvoker? receptorInvoker, CancellationToken cancellationToken) {
    if (_lifecycleMessageDeserializer is null || receptorInvoker is null) {
      return;
    }

    foreach (var work in myWork) {
      var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
      var typedEnvelope = work.Envelope.ReconstructWithPayload(message);
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PreInboxDetached,
        EventId = null,
        StreamId = null,
        LastProcessedEventId = null,
        MessageSource = MessageSource.Inbox,
        AttemptNumber = null
      };

      // Detached: fire-and-forget with own DI scope (does not block pipeline)
      _fireDetachedStageAsync(typedEnvelope, LifecycleStage.PreInboxDetached, lifecycleContext, cancellationToken);
      // Inline: blocks pipeline until all receptors complete
      lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PreInboxInline };
      await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PreInboxInline, lifecycleContext, cancellationToken);
      await _invokeImmediateDetachedAsync(receptorInvoker, typedEnvelope, lifecycleContext, cancellationToken);
    }
  }

  private async Task _processInboxWorkItemsAsync(
    List<InboxWork> myWork, IWorkCoordinatorStrategy strategy, CancellationToken cancellationToken) {
    await _orderedProcessor.ProcessInboxWorkAsync(
      myWork,
      processor: async (work) => {
        _ = _deserializeEvent(work);
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
  }

  private async Task _invokePostInboxLifecycleAsync(
    List<InboxWork> myWork, IReceptorInvoker? receptorInvoker,
    IServiceProvider scopedProvider, CancellationToken cancellationToken) {
    if (_lifecycleMessageDeserializer is null || receptorInvoker is null) {
      return;
    }

    foreach (var work in myWork) {
      var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
      var typedEnvelope = work.Envelope.ReconstructWithPayload(message);
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PostInboxDetached,
        EventId = null,
        StreamId = null,
        LastProcessedEventId = null,
        MessageSource = MessageSource.Inbox,
        AttemptNumber = null
      };

      // Detached: fire-and-forget with own DI scope (does not block pipeline)
      _fireDetachedStageAsync(typedEnvelope, LifecycleStage.PostInboxDetached, lifecycleContext, cancellationToken);
      // Inline: blocks pipeline until all receptors complete
      lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostInboxInline };
      await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostInboxInline, lifecycleContext, cancellationToken);
      await _invokeImmediateDetachedAsync(receptorInvoker, typedEnvelope, lifecycleContext, cancellationToken);

      if (_isEventWithoutPerspectives(work.MessageType, scopedProvider)) {
        await InvokePostLifecycleForEventAsync(work, typedEnvelope, receptorInvoker, lifecycleContext, scopedProvider, cancellationToken, _detachedTasks.Add);
      }
    }
  }

  internal static async Task InvokePostLifecycleForEventAsync(
    InboxWork work, IMessageEnvelope typedEnvelope, IReceptorInvoker receptorInvoker,
    LifecycleExecutionContext lifecycleContext, IServiceProvider scopedProvider,
    CancellationToken cancellationToken, Action<Task>? trackDetachedTask = null) {
    var coordinator = scopedProvider.GetService<ILifecycleCoordinator>();
    if (coordinator is not null) {
      var eventId = work.Envelope.MessageId.Value;
      var tracking = coordinator.BeginTracking(
        eventId, typedEnvelope, LifecycleStage.PostAllPerspectivesDetached, MessageSource.Inbox);
      await tracking.AdvanceToAsync(LifecycleStage.PostAllPerspectivesDetached, scopedProvider, cancellationToken);
      await tracking.AdvanceToAsync(LifecycleStage.PostAllPerspectivesInline, scopedProvider, cancellationToken);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleDetached, scopedProvider, cancellationToken);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scopedProvider, cancellationToken);
      coordinator.AbandonTracking(eventId);
    } else {
      // Detached stages: fire-and-forget with own DI scope
      var scopeFactory = scopedProvider.GetRequiredService<IServiceScopeFactory>();
      var detached1 = _fireDetachedStageStaticAsync(scopeFactory, typedEnvelope, LifecycleStage.PostAllPerspectivesDetached, lifecycleContext);
      trackDetachedTask?.Invoke(detached1);

      lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostAllPerspectivesInline };
      await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostAllPerspectivesInline, lifecycleContext, cancellationToken);
      await _invokeImmediateDetachedAsync(receptorInvoker, typedEnvelope, lifecycleContext, cancellationToken);

      var detached2 = _fireDetachedStageStaticAsync(scopeFactory, typedEnvelope, LifecycleStage.PostLifecycleDetached, lifecycleContext);
      trackDetachedTask?.Invoke(detached2);

      lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostLifecycleInline };
      await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostLifecycleInline, lifecycleContext, cancellationToken);
      await _invokeImmediateDetachedAsync(receptorInvoker, typedEnvelope, lifecycleContext, cancellationToken);
    }
  }

  private static async Task _invokeImmediateDetachedAsync(IReceptorInvoker receptorInvoker, IMessageEnvelope typedEnvelope, LifecycleExecutionContext lifecycleContext, CancellationToken cancellationToken) {
    await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateDetached,
      lifecycleContext with { CurrentStage = LifecycleStage.ImmediateDetached }, cancellationToken);
  }

  /// <summary>
  /// Fires a Detached lifecycle stage as fire-and-forget with its own DI scope.
  /// The receptor runs independently on the thread pool — if it calls WaitForStreamAsync,
  /// it blocks its own task, not the worker pipeline.
  /// ImmediateDetached is chained in the same closure.
  /// </summary>
  private void _fireDetachedStageAsync(
      IMessageEnvelope envelope, LifecycleStage stage,
      LifecycleExecutionContext context, CancellationToken ct) {
    var task = Task.Run(async () => {
      try {
        await using var scope = _scopeFactory.CreateAsyncScope();
        await SecurityContextHelper.EstablishFullContextAsync(envelope, scope.ServiceProvider, ct);
        var invoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
        if (invoker is null) {
          return;
        }
        var ctx = context with { CurrentStage = stage };
        await invoker.InvokeAsync(envelope, stage, ctx, ct);
        await invoker.InvokeAsync(envelope, LifecycleStage.ImmediateDetached,
          ctx with { CurrentStage = LifecycleStage.ImmediateDetached }, ct);
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        // Graceful shutdown — suppress
      } catch (Exception ex) {
        LogDetachedStageError(_logger, ex, stage, envelope.MessageId.Value);
      }
    }, ct);
    _detachedTasks.Add(task);
  }

  /// <summary>
  /// Waits for all in-flight detached tasks to complete.
  /// Used for graceful shutdown and testing.
  /// </summary>
  internal async ValueTask DrainDetachedAsync() {
    await Task.WhenAll(_detachedTasks).ConfigureAwait(false);
  }

  /// <summary>
  /// Static fire-and-forget for Detached stages in static methods (fallback path).
  /// Returns the Task for tracking/draining in tests.
  /// </summary>
  private static Task _fireDetachedStageStaticAsync(
      IServiceScopeFactory scopeFactory, IMessageEnvelope envelope,
      LifecycleStage stage, LifecycleExecutionContext context) {
    return Task.Run(async () => {
      try {
        await using var scope = scopeFactory.CreateAsyncScope();
        await SecurityContextHelper.EstablishFullContextAsync(envelope, scope.ServiceProvider, default);
        var invoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
        if (invoker is null) {
          return;
        }
        var ctx = context with { CurrentStage = stage };
        await invoker.InvokeAsync(envelope, stage, ctx, default);
        await invoker.InvokeAsync(envelope, LifecycleStage.ImmediateDetached,
          ctx with { CurrentStage = LifecycleStage.ImmediateDetached }, default);
      } catch (OperationCanceledException) {
        // Graceful shutdown
#pragma warning disable RCS1075 // No logger in static context; errors surface via receptor telemetry
      } catch (Exception) {
#pragma warning restore RCS1075
        // Static context — errors surface via receptor telemetry
      }
    });
  }

  /// <summary>
  /// Checks if the given message type is an event type that has NO associated perspectives.
  /// Events with perspectives get PostLifecycle from PerspectiveWorker at batch end.
  /// Events without perspectives need PostLifecycle fired here immediately.
  /// </summary>
  private static bool _isEventWithoutPerspectives(string messageType, IServiceProvider serviceProvider) {
    var registry = serviceProvider.GetService<IPerspectiveRunnerRegistry>();
    if (registry is null) {
      // No perspectives registered at all - all events are "without perspectives"
      return true;
    }

    var normalizedMessageType = EventTypeMatchingHelper.NormalizeTypeName(messageType);

    var perspectives = registry.GetRegisteredPerspectives();
    foreach (var perspective in perspectives) {
      foreach (var eventType in perspective.EventTypes) {
        var normalizedEventType = EventTypeMatchingHelper.NormalizeTypeName(eventType);
        if (string.Equals(normalizedMessageType, normalizedEventType, StringComparison.Ordinal)) {
          return false; // This event type has at least one perspective
        }
      }
    }

    return true; // No perspectives handle this event type
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
      var serializer = scopeServiceProvider.GetService<IEnvelopeSerializer>()
        ?? throw new InvalidOperationException("IEnvelopeSerializer is required but not registered");

      // Call generic SerializeEnvelope method via reflection
      var genericMethod = typeof(IEnvelopeSerializer).GetMethod(nameof(IEnvelopeSerializer.SerializeEnvelope));
      var boundMethod = genericMethod!.MakeGenericMethod(payloadType);
      var serialized = (SerializedEnvelope)boundMethod.Invoke(serializer, [envelope])!;
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
    var simpleTypeName = TypeNameFormatter.GetSimpleName(messageTypeName);
    var handlerName = simpleTypeName + "Handler";

    var streamId = _extractStreamId(envelope);

    // Guard: fail-fast if StreamId is Guid.Empty for events
    if (isEvent) {
      StreamIdGuard.ThrowIfEmpty(streamId, envelope.MessageId.Value, "TransportConsumer.Inbox", messageTypeName);
    }

    return new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = handlerName,
      Envelope = jsonEnvelope,
      EnvelopeType = envelopeTypeFromTransport,
      StreamId = streamId,
      IsEvent = isEvent,
      Scope = envelope.GetCurrentScope()?.Scope,
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
    // Defensive: Handle null Hops gracefully
    var firstHop = envelope.Hops?.FirstOrDefault();
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

    // S3267: Loop contains await — LINQ doesn't support async lambdas
#pragma warning disable S3267
    foreach (var state in _states.Values) {
      if (state.Subscription != null) {
        await state.Subscription.PauseAsync();
      }
    }
#pragma warning restore S3267

    _logger.LogInformation("All subscriptions paused");
  }

  /// <summary>
  /// Resumes all paused subscriptions.
  /// Message processing will continue.
  /// </summary>
  public async Task ResumeAllSubscriptionsAsync() {
    _logger.LogInformation("Resuming all subscriptions");

    // S3267: Loop contains await — LINQ doesn't support async lambdas
#pragma warning disable S3267
    foreach (var state in _states.Values) {
      if (state.Subscription != null) {
        await state.Subscription.ResumeAsync();
      }
    }
#pragma warning restore S3267

    _logger.LogInformation("All subscriptions resumed");
  }

  /// <summary>
  /// Stops the worker and disposes all subscriptions.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  public override async Task StopAsync(CancellationToken cancellationToken) {
    _logger.LogInformation("Stopping TransportConsumerWorker");

    if (_linkedCts is not null) {
      await _linkedCts.CancelAsync();
    }
    _linkedCts?.Dispose();

    // Dispose all subscriptions
    foreach (var state in _states.Values) {
      state.Subscription?.Dispose();
    }

    _states.Clear();

    _logger.LogInformation("TransportConsumerWorker stopped");

    await base.StopAsync(cancellationToken);
  }

  /// <summary>
  /// Populates DeliveredAt timestamp properties on the message payload using JSON manipulation.
  /// AOT-safe: uses JsonNode, no reflection or Type.GetType().
  /// </summary>
  private static void _populateDeliveredAtTimestamp(IMessageEnvelope envelope, string? envelopeType) {
    if (envelopeType is null || envelope is not MessageEnvelope<JsonElement> concreteEnvelope) {
      return;
    }

    var messageTypeName = _extractMessageTypeFromEnvelopeType(envelopeType);
    concreteEnvelope.Payload = JsonAutoPopulateHelper.PopulateTimestampByName(
        concreteEnvelope.Payload,
        messageTypeName,
        TimestampKind.DeliveredAt,
        DateTimeOffset.UtcNow);
  }

  /// <summary>Logs that a message was dropped during shutdown due to ObjectDisposedException.</summary>
  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Message {MessageId} dropped during shutdown (ObjectDisposedException)"
  )]
  private static partial void LogMessageDroppedDuringShutdown(ILogger logger, Guid messageId);

  /// <summary>Logs that a self-echo message was discarded (owned event from this service arriving back via transport).</summary>
  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Self-echo discarded: {MessageType} from {ServiceName}"
  )]
  private static partial void LogSelfEchoDiscarded(ILogger logger, string messageType, string serviceName);

  /// <summary>Logs that a detached lifecycle stage failed during fire-and-forget execution.</summary>
  [LoggerMessage(
    Level = LogLevel.Error,
    Message = "Detached lifecycle stage {Stage} failed for message {MessageId}"
  )]
  private static partial void LogDetachedStageError(ILogger logger, Exception ex, LifecycleStage stage, Guid? messageId);

  /// <summary>
  /// Checks if the message originated from this service (self-echo).
  /// The last hop is the most recent — it identifies the service that dispatched the message.
  /// </summary>
  private bool _isSelfEcho(IMessageEnvelope envelope) {
    if (_serviceName is null || envelope.Hops.Count == 0) {
      return false;
    }
    var lastHop = envelope.Hops[^1];
    return string.Equals(lastHop.ServiceInstance.ServiceName, _serviceName, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Checks if the given namespace is owned by this service.
  /// Supports hierarchical matching: "A.B" owns "A.B.C.D".
  /// </summary>
  private bool _isOwnedNamespace(string? ns) {
    if (string.IsNullOrEmpty(ns) || _ownedDomains.Count == 0) {
      return false;
    }
    if (_ownedDomains.Contains(ns)) {
      return true;
    }
    foreach (var owned in _ownedDomains) {
      var prefix = owned.EndsWith('.') ? owned : owned + ".";
      if (ns.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
    }
    return false;
  }

  // Namespace extraction moved to TypeNameFormatter.GetPayloadNamespace (testable utility)
}
