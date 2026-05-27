using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Lenses;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background service that subscribes to messages from Azure Service Bus and invokes local perspectives.
/// Uses work coordinator pattern for atomic deduplication and stream-based ordering.
/// Events from remote services are stored in inbox via process_work_batch and perspectives are invoked with ordering guarantees.
/// </summary>
/// <docs>messaging/transports/transport-consumer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerSecurityContextTests.cs</tests>
#pragma warning disable S107 // Constructor uses DI injection — many parameters are idiomatic
public partial class ServiceBusConsumerWorker(
  ITransport transport,
  IServiceScopeFactory scopeFactory,
  JsonSerializerOptions jsonOptions,
  ILogger<ServiceBusConsumerWorker> logger,
  OrderedStreamProcessor orderedProcessor,
  ServiceBusConsumerOptions? options = null,
  ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
  IEnvelopeSerializer? envelopeSerializer = null,
  MessageProcessingOptions? messageProcessingOptions = null,
  IReceptorRegistryQuery? receptorRegistry = null,
  IReceptorRegistry? runtimeReceptorRegistry = null
  ) : BackgroundService {
#pragma warning restore S107
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ConcurrentBag<Task> _detachedTasks = [];
  private readonly ILogger<ServiceBusConsumerWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly OrderedStreamProcessor _orderedProcessor = orderedProcessor ?? throw new ArgumentNullException(nameof(orderedProcessor));
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
  private readonly IEnvelopeSerializer? _envelopeSerializer = envelopeSerializer;
  private readonly IReceptorRegistryQuery? _receptorRegistry = receptorRegistry;
  private readonly IReceptorRegistry? _runtimeReceptorRegistry = runtimeReceptorRegistry;
  private readonly SemaphoreSlim? _concurrencySemaphore = (messageProcessingOptions?.MaxConcurrentMessages ?? 40) > 0
    ? new SemaphoreSlim(messageProcessingOptions?.MaxConcurrentMessages ?? 40) : null;
  private readonly List<ISubscription> _subscriptions = [];
  private readonly ServiceBusConsumerOptions _options = options ?? new ServiceBusConsumerOptions();

  // Stream-affinity per-stream serializer — slice 2 of plans/stream-affinity-everywhere.md.
  // Routes received messages by stream_id so same-stream items process serially under one
  // worker even when the transport delivers them across many parallel consumer threads.
  // Different streams continue to run in parallel via independent per-stream workers.
  private readonly PerStreamSerializer<AsbReceivedItem> _streamSerializer = new(
    streamIdSelector: static item => _extractStreamId(item.Envelope),
    processor: static (item, ct) => item.HandleAsync(ct));

  private sealed record AsbReceivedItem(
    IMessageEnvelope Envelope,
    string? EnvelopeType,
    Func<IMessageEnvelope, string?, CancellationToken, Task> Handler,
    TaskCompletionSource Done) {
    public async Task HandleAsync(CancellationToken ct) {
      try {
        await Handler(Envelope, EnvelopeType, ct).ConfigureAwait(false);
        Done.TrySetResult();
      } catch (Exception ex) {
        Done.TrySetException(ex);
      }
    }
  }

  /// <summary>
  /// Pauses all subscriptions to temporarily stop receiving messages.
  /// Useful for test cleanup scenarios where draining is needed without competing consumers.
  /// </summary>
  public async Task PauseAllSubscriptionsAsync() {
    foreach (var subscription in _subscriptions) {
      await subscription.PauseAsync();
    }
  }

  /// <summary>
  /// Resumes all subscriptions to continue receiving messages.
  /// </summary>
  public async Task ResumeAllSubscriptionsAsync() {
    foreach (var subscription in _subscriptions) {
      await subscription.ResumeAsync();
    }
  }

  /// <summary>
  /// Starts the worker and creates all subscriptions BEFORE background processing begins.
  /// This ensures subscriptions are ready before ExecuteAsync runs (blocking initialization).
  /// </summary>
  public override async Task StartAsync(CancellationToken cancellationToken) {
    using var activity = WhizbangActivitySource.Hosting.StartActivity("ServiceBusConsumerWorker.Start");
    activity?.SetTag("worker.subscriptions_count", _options.Subscriptions.Count);
    activity?.SetTag("servicebus.has_filter", _options.Subscriptions.Any(s => !string.IsNullOrWhiteSpace(s.DestinationFilter)));

    LogWorkerStarting(_logger);

    try {
      // Subscribe to configured topics (BLOCKING - ensures subscriptions ready before ExecuteAsync)
      foreach (var topicConfig in _options.Subscriptions) {
        // Create destination with DestinationFilter metadata if specified
        var metadata = !string.IsNullOrWhiteSpace(topicConfig.DestinationFilter)
          ? new Dictionary<string, JsonElement> { ["DestinationFilter"] = JsonElementHelper.FromString(topicConfig.DestinationFilter) }
          : null;

        var destination = new TransportDestination(
          topicConfig.TopicName,
          topicConfig.SubscriptionName,
          metadata
        );

        var subscription = await _transport.SubscribeBatchAsync(
          async (batch, ct) => {
            // Stream-affinity routing: enqueue all batch messages to _streamSerializer first
            // (keyed by _extractStreamId), then await all completions concurrently. This
            // preserves cross-stream parallelism — if message #1 goes to stream A and message
            // #2 goes to stream B, B's worker starts immediately even if A's hangs.
            //
            // Earlier code did `await _streamSerializer.EnqueueAsync; await done.Task` per
            // message in a foreach, which serialized the entire batch by accident: a hung A
            // message blocked B from even entering the serializer (JDX 2026-05-05 incident —
            // 368 inbox messages stuck because one stream's processing stalled).
            //
            // Per-message TaskCompletionSource preserves transport ack/nack semantics: handler
            // success → SetResult; exception → SetException → propagates to the ASB callback
            // for redelivery via WhenAll. WhenAll surfaces the first exception once all tasks
            // complete (success or fault), matching the previous foreach-await semantics.
            var pending = new List<Task>(batch.Count);
            foreach (var msg in batch) {
              var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
              await _streamSerializer.EnqueueAsync(
                new AsbReceivedItem(msg.Envelope, msg.EnvelopeType, _handleMessageAsync, done), ct);
              pending.Add(done.Task);
            }
            if (pending.Count > 0) {
              await Task.WhenAll(pending).WaitAsync(ct);
            }
          },
          destination,
          new TransportBatchOptions(),
          cancellationToken
        );

        _subscriptions.Add(subscription);

        LogSubscribedToTopic(_logger, topicConfig.TopicName, topicConfig.SubscriptionName);
      }

      LogSubscriptionsReady(_logger, _subscriptions.Count);

      // Call base.StartAsync to trigger ExecuteAsync
      await base.StartAsync(cancellationToken);
    } catch (Exception ex) {
      LogFailedToStart(_logger, ex);
      throw;
    }
  }

  /// <summary>
  /// Background processing loop - keeps worker alive while subscriptions process messages.
  /// Subscriptions are already created in StartAsync (blocking), so this just waits.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogBackgroundProcessingStarted(_logger);

    try {
      // Keep the worker running while subscriptions are active
      await Task.Delay(Timeout.Infinite, stoppingToken);
    } catch (OperationCanceledException) {
      LogWorkerStopping(_logger);
    } catch (Exception ex) {
      LogFatalError(_logger, ex);
      throw;
    }
  }

  private async Task _handleMessageAsync(IMessageEnvelope envelope, string? envelopeType, CancellationToken ct) {
    // Slice 3 of pump-then-process.md (Half A): drop messages whose type has NO consumer
    // anywhere on this service — no inbox handler, no lifecycle receptor, no perspective,
    // no tag-attribute. They cannot do useful work, so the inbox row + dispatch path are
    // pure waste. Dropping at the receive boundary keeps wh_inbox clean for the JDX BFF
    // pattern where many cross-service events flow through with no local consumer.
    // Null registry → legacy behavior (always store) for back-compat in test harnesses.
    // EnvelopeTypeNameHelper.ExtractInnerTypeName returns null on unwrapped formats; we
    // skip the gate in that case rather than misclassifying — never drop a message
    // because of a registry-side parse miss.
    if (_receptorRegistry is not null && !string.IsNullOrWhiteSpace(envelopeType)) {
      var innerMessageType = EnvelopeTypeNameHelper.ExtractInnerTypeName(envelopeType);
      if (innerMessageType is not null && !_receptorRegistry.HasAnyConsumer(innerMessageType)) {
        LogDroppedUnsubscribedType(_logger, envelope.MessageId, innerMessageType);
        return;
      }
    }

    var inboxActivity = _startInboxActivity(envelope, envelopeType);

    // Global concurrency gate — limits total concurrent handlers across all subscriptions
    if (_concurrencySemaphore is not null) {
      await _concurrencySemaphore.WaitAsync(ct);
    }

    try { // semaphore is released in finally block
      await using var scope = _scopeFactory.CreateAsyncScope();
      var scopedProvider = scope.ServiceProvider;
      await SecurityContextHelper.EstablishFullContextAsync(envelope, scopedProvider, ct);
      var strategy = scopedProvider.GetRequiredService<IWorkCoordinatorStrategy>();
      LogProcessingMessage(_logger, envelope.MessageId);

      // Serialize and deduplicate via per-message flush
      var myWork = await _serializeAndDeduplicateAsync(envelope, envelopeType, strategy, scopedProvider, ct);

      if (myWork.Count == 0) {
        LogMessageAlreadyProcessed(_logger, envelope.MessageId);
        return;
      }
      LogMessageAcceptedForProcessing(_logger, envelope.MessageId, myWork.Count);

      // 2. PreInbox lifecycle, process work, PostInbox lifecycle
      var receptorInvoker = scopedProvider.GetService<IReceptorInvoker>();
      await _invokePreInboxLifecycleAsync(myWork, receptorInvoker, ct);
      await _processInboxWorkItemsAsync(myWork, strategy, ct);
      await _invokePostInboxLifecycleAsync(myWork, receptorInvoker, scopedProvider, ct);

      // 3. Report completions/failures back to database (fire-and-forget)
      await strategy.FlushAsync(WorkBatchOptions.SkipInboxClaiming, ct);
      LogSuccessfullyProcessedMessage(_logger, envelope.MessageId);
      inboxActivity?.SetStatus(ActivityStatusCode.Ok);
    } catch (Exception ex) {
      inboxActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
      inboxActivity?.SetTag("exception.type", ex.GetType().FullName);
      inboxActivity?.SetTag("exception.message", ex.Message);
      LogErrorProcessingMessage(_logger, envelope.MessageId, ex);
      throw;
    } finally {
      _concurrencySemaphore?.Release();
      inboxActivity?.Dispose();
    }
  }

  /// <summary>
  /// Starts a distributed trace activity linked to the sender's span via TraceParent.
  /// </summary>
  private static Activity? _startInboxActivity(IMessageEnvelope envelope, string? envelopeType) {
    var traceParent = envelope.Hops
      .Where(h => h.Type == HopType.Current)
      .Select(h => h.TraceParent)
      .LastOrDefault(tp => tp is not null);

    if (traceParent is null || !ActivityContext.TryParse(traceParent, null, out var parentContext)) {
      return null;
    }

    var messageType = envelopeType is not null ? TypeNameFormatter.GetSimpleName(envelopeType) : "Unknown";
    var activity = WhizbangActivitySource.Transport.StartActivity(
      $"Inbox {messageType}", ActivityKind.Consumer, parentContext);
    activity?.SetTag("messaging.message_id", envelope.MessageId.ToString());
    activity?.SetTag("messaging.operation", "receive");
    activity?.SetTag("whizbang.hop_count", envelope.Hops?.Count ?? 0);
    return activity;
  }

  /// <summary>
  /// Serializes envelope to InboxMessage and flushes through work coordinator for deduplication.
  /// </summary>
  private async Task<List<InboxWork>> _serializeAndDeduplicateAsync(
    IMessageEnvelope envelope, string? envelopeType,
    IWorkCoordinatorStrategy strategy, IServiceProvider scopedProvider, CancellationToken ct) {
    var newInboxMessage = _serializeToNewInboxMessage(envelope, envelopeType, scopedProvider);
    strategy.QueueInboxMessage(newInboxMessage);
    LogBeforeFlush(_logger, newInboxMessage.MessageId, newInboxMessage.IsEvent, newInboxMessage.StreamId);
    var workBatch = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.SkipInboxClaiming, ct);
    LogAfterFlush(_logger, workBatch.InboxWork.Count, workBatch.OutboxWork.Count, workBatch.PerspectiveWork.Count);
    var myWork = workBatch.InboxWork.Where(w => w.MessageId == envelope.MessageId.Value).ToList();
    LogWorkReturned(_logger, envelope.MessageId.Value, myWork.Count, newInboxMessage.IsEvent);
    return myWork;
  }

  /// <summary>
  /// Invokes PreInbox lifecycle stages (PreInboxDetached + PreInboxInline) for all work items.
  /// </summary>
  private async Task _invokePreInboxLifecycleAsync(
    List<InboxWork> myWork, IReceptorInvoker? receptorInvoker, CancellationToken ct) {
    if (receptorInvoker is null || _lifecycleMessageDeserializer is null) {
      return;
    }

    foreach (var work in myWork) {
      // Deserialize before the gate so the runtime-registry check can use the concrete
      // payload type — the runtime registry keys by Type, not by string. Costs one extra
      // JSON parse on the cross-service no-handler path versus the pre-fix slice-4 gate,
      // but the alternative was the per-type runtime check missing because we had no
      // Type to ask about. Loss is bounded: PostInbox below already deserializes
      // unconditionally, so worst-case we go from 1 to 2 parses per message.
      var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
      var typedEnvelope = work.Envelope.ReconstructWithPayload(message);

      // Slice 4-symmetry gate: skip PreInbox firing when neither stage has receptors —
      // either compile-time (source-generated WhizbangReceptorRegistryQuery) OR
      // runtime-registered via IReceptorRegistry. Without the runtime branch, services
      // whose generated contribution emits empty arrays for the PreInbox stages would
      // silently fail to fire runtime-registered receptors (integration-test waits,
      // dynamic registrations). Mirrors the InboxDispatchWorker gate fix; null
      // registries preserve legacy fire-unconditionally behavior for test harnesses.
      var runtimeMessageType = typedEnvelope.Payload?.GetType();
      if (_receptorRegistry is not null
          && !_receptorRegistry.HasReceptors(LifecycleStage.PreInboxDetached, work.MessageType)
          && !_receptorRegistry.HasReceptors(LifecycleStage.PreInboxInline, work.MessageType)
          && !_runtimeHasReceptors(runtimeMessageType, LifecycleStage.PreInboxDetached)
          && !_runtimeHasReceptors(runtimeMessageType, LifecycleStage.PreInboxInline)) {
        continue;
      }
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PreInboxDetached,
        EventId = null,
        StreamId = null,
        LastProcessedEventId = null,
        MessageSource = MessageSource.Inbox,
        AttemptNumber = null
      };

      _fireDetachedStageAsync(typedEnvelope, LifecycleStage.PreInboxDetached, lifecycleContext, ct);
      lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PreInboxInline };
      await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PreInboxInline, lifecycleContext, ct);
      await _invokeImmediateDetachedAsync(receptorInvoker, typedEnvelope, lifecycleContext, ct);
    }
  }

  /// <summary>
  /// Processes inbox work items through the OrderedStreamProcessor for stream ordering.
  /// </summary>
  private async Task _processInboxWorkItemsAsync(
    List<InboxWork> myWork, IWorkCoordinatorStrategy strategy, CancellationToken ct) {
    // Slice 5 of plans/pump-then-process.md: dropped the discarded
    // `_ = _deserializeEvent(work)` call. The result was always thrown away with a discard
    // assignment; pure waste at high message volume. Each per-message deserialize cost
    // ~50-200µs on a typical event. For a 350-message bulk fan-out that's 50-150ms saved.
    // The completion-marking and failure-routing semantics are unchanged.
    await _orderedProcessor.ProcessInboxWorkAsync(
      myWork,
      processor: (_) => Task.FromResult(MessageProcessingStatus.EventStored),
      completionHandler: (msgId, status) => {
        strategy.QueueInboxCompletion(msgId, status);
        LogQueuedCompletion(_logger, msgId, status);
      },
      failureHandler: (msgId, status, error) => {
        strategy.QueueInboxFailure(msgId, status, error);
        LogQueuedFailure(_logger, msgId, error);
      },
      ct
    );
  }


  /// <summary>
  /// Invokes PostInbox lifecycle stages and PostLifecycle for events without perspectives.
  /// </summary>
  private async Task _invokePostInboxLifecycleAsync(
    List<InboxWork> myWork, IReceptorInvoker? receptorInvoker,
    IServiceProvider scopedProvider, CancellationToken ct) {
    if (receptorInvoker is null || _lifecycleMessageDeserializer is null) {
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

      _fireDetachedStageAsync(typedEnvelope, LifecycleStage.PostInboxDetached, lifecycleContext, ct);
      lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostInboxInline };
      await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostInboxInline, lifecycleContext, ct);
      await _invokeImmediateDetachedAsync(receptorInvoker, typedEnvelope, lifecycleContext, ct);

      if (_isEventWithoutPerspectives(work.MessageType, scopedProvider)) {
        await _invokePostLifecycleForEventAsync(work, typedEnvelope, receptorInvoker, lifecycleContext, scopedProvider, ct, _detachedTasks.Add);
      }
    }
  }

  /// <summary>
  /// Invokes PostLifecycle stages for an event without perspectives.
  /// </summary>
  private static async Task _invokePostLifecycleForEventAsync(
    InboxWork work, IMessageEnvelope typedEnvelope, IReceptorInvoker receptorInvoker,
    LifecycleExecutionContext lifecycleContext, IServiceProvider scopedProvider, CancellationToken ct,
    Action<Task>? trackDetachedTask = null) {
    var coordinator = scopedProvider.GetService<ILifecycleCoordinator>();
    if (coordinator is not null) {
      var eventId = work.Envelope.MessageId.Value;
      var tracking = coordinator.BeginTracking(
        eventId, typedEnvelope, LifecycleStage.PostLifecycleDetached, MessageSource.Inbox);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleDetached, scopedProvider, ct);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scopedProvider, ct);
      // Wait for detached tasks to complete before abandoning tracking
      // This ensures PostLifecycleDetached (which fires in Task.Run) completes
      // before the worker's DrainDetachedAsync returns.
      await tracking.DrainDetachedAsync();
      coordinator.AbandonTracking(eventId);
    } else {
      var scopeFactory = scopedProvider.GetRequiredService<IServiceScopeFactory>();
      var detachedTask = _fireDetachedStageStaticAsync(scopeFactory, typedEnvelope, LifecycleStage.PostLifecycleDetached, lifecycleContext);
      trackDetachedTask?.Invoke(detachedTask);

      lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostLifecycleInline };
      await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostLifecycleInline, lifecycleContext, ct);
      await _invokeImmediateDetachedAsync(receptorInvoker, typedEnvelope, lifecycleContext, ct);
    }
  }

  private static async Task _invokeImmediateDetachedAsync(IReceptorInvoker receptorInvoker, IMessageEnvelope typedEnvelope, LifecycleExecutionContext lifecycleContext, CancellationToken ct) {
    await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateDetached,
      lifecycleContext with { CurrentStage = LifecycleStage.ImmediateDetached }, ct);
  }

  private bool _runtimeHasReceptors(Type? messageType, LifecycleStage stage) {
    if (_runtimeReceptorRegistry is null || messageType is null) {
      return false;
    }
    return _runtimeReceptorRegistry.GetReceptorsFor(messageType, stage).Count > 0;
  }

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
        // Graceful shutdown
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
#pragma warning disable RCS1075 // No logger in static context
      } catch (Exception) {
#pragma warning restore RCS1075
        // Errors surface via receptor telemetry
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
  /// The actual type information is preserved in envelopeTypeFromTransport for later deserialization.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  private InboxMessage _serializeToNewInboxMessage(IMessageEnvelope envelope, string? envelopeTypeFromTransport, IServiceProvider scopeServiceProvider) {
    // Envelopes from transport can be:
    // 1. Strongly-typed: MessageEnvelope<ProductCreatedEvent> - needs serialization to JsonElement form
    // 2. JsonElement-typed: MessageEnvelope<JsonElement> - already in storage form

    if (string.IsNullOrWhiteSpace(envelopeTypeFromTransport)) {
      throw new InvalidOperationException(
        $"EnvelopeType is required from transport but was null/empty. MessageId: {envelope.MessageId}. " +
        "This indicates a bug in the transport layer - envelope type must be preserved during transmission.");
    }

    // Extract message type from envelope type string
    // Example: "MessageEnvelope`1[[MyApp.ProductCreatedEvent, MyApp]], Whizbang.Core"
    // We need to extract: "MyApp.ProductCreatedEvent, MyApp"
    var messageTypeName = _extractMessageTypeFromEnvelopeType(envelopeTypeFromTransport);

    // Get payload to check its type
    var payload = envelope.Payload;
    var payloadType = payload?.GetType() ?? typeof(object);

    // Check if envelope/payload is already in JsonElement form
    // CRITICAL: Must check payload type, not just envelope type, because envelope could be
    // IMessageEnvelope<object> with a JsonElement payload
    IMessageEnvelope<JsonElement> jsonEnvelope;
    if (envelope is IMessageEnvelope<JsonElement> alreadyJsonEnvelope) {
      // Envelope is correctly typed as IMessageEnvelope<JsonElement> - use directly
      jsonEnvelope = alreadyJsonEnvelope;
    } else if (payloadType == typeof(JsonElement)) {
      // Payload is JsonElement but envelope is not IMessageEnvelope<JsonElement>
      // This means envelope is probably IMessageEnvelope<object> with JsonElement payload
      // DEFENSIVE: This should not happen - envelopes from transport should be strongly-typed
      throw new InvalidOperationException(
        $"Envelope has JsonElement payload but envelope type is {envelope.GetType().Name}. " +
        $"MessageId: {envelope.MessageId}. " +
        "This indicates double-serialization or incorrect envelope creation. " +
        "Envelopes from transport must be strongly-typed (e.g., MessageEnvelope<ProductCreatedEvent>), " +
        "not MessageEnvelope<object> or MessageEnvelope<JsonElement>.");
    } else {
      // Strongly-typed envelope - need to serialize it to JsonElement form for storage
      var serializer = _envelopeSerializer ?? scopeServiceProvider.GetService<IEnvelopeSerializer>()
        ?? throw new InvalidOperationException(
          "IEnvelopeSerializer is required but not registered. " +
          "Ensure you call services.AddWhizbang() to register core services.");

      // Call generic SerializeEnvelope method via reflection (necessary because payload type is only known at runtime)
      var genericEnvelopeMethod = typeof(IEnvelopeSerializer).GetMethod(nameof(IEnvelopeSerializer.SerializeEnvelope));
      var boundMethod = genericEnvelopeMethod!.MakeGenericMethod(payloadType);
      var serialized = (SerializedEnvelope)boundMethod.Invoke(serializer, [envelope])!;
      jsonEnvelope = serialized.JsonEnvelope;

      // NOTE: We use envelopeTypeFromTransport instead of serialized.EnvelopeType
      // because the transport's metadata is authoritative
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

    // Extract simple type name for handler name
    var simpleTypeName = TypeNameFormatter.GetSimpleName(messageTypeName);
    var handlerName = simpleTypeName + "Handler";

    var streamId = _extractStreamId(envelope);

    // Guard: fail-fast if StreamId is Guid.Empty for events
    if (isEvent) {
      StreamIdGuard.ThrowIfEmpty(streamId, envelope.MessageId.Value, "ServiceBusConsumer.Inbox", messageTypeName);
    }

    LogSerializeInboxMessage(_logger, envelope.MessageId.Value, simpleTypeName, isEvent, streamId);

    var inboxMessage = new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = handlerName,
      Envelope = jsonEnvelope,
      EnvelopeType = envelopeTypeFromTransport,  // Use the original type from transport!
      StreamId = streamId,
      IsEvent = isEvent,
      Scope = envelope.GetCurrentScope()?.Scope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? [],
        DispatchContext = envelope.DispatchContext
      },
      MessageType = messageTypeName
    };

    LogCreatedInboxMessage(_logger, inboxMessage.MessageId, inboxMessage.IsEvent, inboxMessage.StreamId,
      inboxMessage.MessageType, inboxMessage.EnvelopeType, jsonEnvelope.Payload.ValueKind);

    return inboxMessage;
  }

  /// <summary>
  /// Extracts the message type name from an envelope type name.
  /// Parses "MessageEnvelope`1[[MyApp.CreateProductCommand, MyApp]], Whizbang.Core"
  /// and returns "MyApp.CreateProductCommand, MyApp".
  /// </summary>
  private static string _extractMessageTypeFromEnvelopeType(string envelopeTypeName) {
    var startIndex = envelopeTypeName.IndexOf("[[", StringComparison.Ordinal);
    var endIndex = envelopeTypeName.IndexOf("]]", StringComparison.Ordinal);

    if (startIndex == -1 || endIndex == -1 || startIndex >= endIndex) {
      throw new InvalidOperationException(
        $"Invalid envelope type name format: '{envelopeTypeName}'. " +
        "Expected format: 'MessageEnvelope`1[[MessageType, Assembly]], EnvelopeAssembly'");
    }

    var messageTypeName = envelopeTypeName.Substring(startIndex + 2, endIndex - startIndex - 2);

    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new InvalidOperationException(
        $"Failed to extract message type name from envelope type: '{envelopeTypeName}'");
    }

    return messageTypeName;
  }

  /// <summary>
  /// Extracts event payload from InboxWork for processing.
  /// Envelope is deserialized as MessageEnvelope&lt;JsonElement&gt;, so we need to deserialize the payload.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  private object? _deserializeEvent(InboxWork work) {
    try {
      // InboxWork envelope is IMessageEnvelope<JsonElement>
      // Deserialize the JsonElement payload back to the actual event type
      var jsonElement = work.Envelope.Payload;

      // Use GetTypeInfoByName from JsonContextRegistry for AOT-safe cross-assembly type lookup
      // This queries all registered type name mappings from all assemblies via ModuleInitializers
      // Supports fuzzy matching on "TypeName, AssemblyName" (strips Version/Culture/PublicKeyToken)
      var jsonTypeInfo = Serialization.JsonContextRegistry.GetTypeInfoByName(work.MessageType, _jsonOptions);
      if (jsonTypeInfo == null) {
        LogCouldNotResolveJsonTypeInfo(_logger, work.MessageType, work.MessageId);
        return null;
      }

      var @event = jsonElement.Deserialize(jsonTypeInfo);
      return @event;
    } catch (Exception ex) {
      LogFailedToDeserializeEvent(_logger, work.MessageId, ex);
      return null;
    }
  }

  /// <summary>
  /// Extracts stream_id from envelope for stream-based ordering.
  /// Uses [StreamId] attribute value stored in metadata as "AggregateId" for backward compatibility.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  private static Guid _extractStreamId(IMessageEnvelope envelope) {
    // Note: Metadata key is "AggregateId" for backward compatibility with existing envelopes
    var firstHop = envelope.Hops?.FirstOrDefault();
    if (firstHop?.Metadata != null && firstHop.Metadata.TryGetValue("AggregateId", out var streamIdElem) &&
        streamIdElem.ValueKind == JsonValueKind.String) {
      var streamIdStr = streamIdElem.GetString();
      if (streamIdStr != null && Guid.TryParse(streamIdStr, out var parsedStreamId)) {
        return parsedStreamId;
      }
    }

    // Fall back to message ID (ensures all messages have a stream)
    return envelope.MessageId.Value;
  }

  /// <summary>
  /// Stops the worker and disposes all subscriptions.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  public override async Task StopAsync(CancellationToken cancellationToken) {
    LogWorkerStoppingGracefully(_logger);

    // Dispose all subscriptions first so no new messages enter the serializer.
    foreach (var subscription in _subscriptions) {
      subscription.Dispose();
    }

    // Drain in-flight per-stream work before exiting.
    await _streamSerializer.FlushAndStopAsync(cancellationToken).ConfigureAwait(false);

    await base.StopAsync(cancellationToken);
  }

  // ========================================
  // High-Performance LoggerMessage Delegates
  // ========================================

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Information,
    Message = "ServiceBusConsumerWorker starting - creating subscriptions..."
  )]
  static partial void LogWorkerStarting(ILogger logger);

  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Information,
    Message = "Subscribed to topic {TopicName} with subscription {SubscriptionName}"
  )]
  static partial void LogSubscribedToTopic(ILogger logger, string topicName, string subscriptionName);

  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Information,
    Message = "ServiceBusConsumerWorker subscriptions ready ({Count} subscriptions)"
  )]
  static partial void LogSubscriptionsReady(ILogger logger, int count);

  [LoggerMessage(
    EventId = 25,
    Level = LogLevel.Debug,
    Message = "Serializing to InboxMessage: MessageId={MessageId}, PayloadType={PayloadType}, IsEvent={IsEvent}, StreamId={StreamId}"
  )]
  static partial void LogSerializeInboxMessage(ILogger logger, Guid messageId, string payloadType, bool isEvent, Guid streamId);

  [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Error,
    Message = "Failed to start ServiceBusConsumerWorker - subscriptions not ready"
  )]
  static partial void LogFailedToStart(ILogger logger, Exception ex);

  [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Information,
    Message = "ServiceBusConsumerWorker background processing started"
  )]
  static partial void LogBackgroundProcessingStarted(ILogger logger);

  [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Information,
    Message = "ServiceBusConsumerWorker is stopping..."
  )]
  static partial void LogWorkerStopping(ILogger logger);

  [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Error,
    Message = "Fatal error in ServiceBusConsumerWorker"
  )]
  static partial void LogFatalError(ILogger logger, Exception ex);

  [LoggerMessage(
    EventId = 8,
    Level = LogLevel.Information,
    Message = "Processing message {MessageId} from Service Bus"
  )]
  static partial void LogProcessingMessage(ILogger logger, MessageId messageId);

  [LoggerMessage(
    EventId = 9,
    Level = LogLevel.Information,
    Message = "Message {MessageId} already processed (duplicate), skipping"
  )]
  static partial void LogMessageAlreadyProcessed(ILogger logger, MessageId messageId);

  [LoggerMessage(
    EventId = 10,
    Level = LogLevel.Debug,
    Message = "Message {MessageId} accepted for processing ({WorkCount} inbox work items)"
  )]
  static partial void LogMessageAcceptedForProcessing(ILogger logger, MessageId messageId, int workCount);

  [LoggerMessage(
    EventId = 11,
    Level = LogLevel.Debug,
    Message = "Invoked perspectives for {EventType} (message {MessageId})"
  )]
  static partial void LogInvokedPerspectives(ILogger logger, string eventType, Guid messageId);

  [LoggerMessage(
    EventId = 12,
    Level = LogLevel.Warning,
    Message = "Failed to invoke perspectives - Event: {EventType}, HasInvoker: {HasInvoker}"
  )]
  static partial void LogFailedToInvokePerspectives(ILogger logger, string eventType, bool hasInvoker);

  [LoggerMessage(
    EventId = 13,
    Level = LogLevel.Debug,
    Message = "Queued completion for {MessageId} with status {Status}"
  )]
  static partial void LogQueuedCompletion(ILogger logger, Guid messageId, MessageProcessingStatus status);

  [LoggerMessage(
    EventId = 14,
    Level = LogLevel.Error,
    Message = "Queued failure for {MessageId}: {Error}"
  )]
  static partial void LogQueuedFailure(ILogger logger, Guid messageId, string error);

  [LoggerMessage(
    EventId = 15,
    Level = LogLevel.Debug,
    Message = "Successfully processed message {MessageId}"
  )]
  static partial void LogSuccessfullyProcessedMessage(ILogger logger, MessageId messageId);

  [LoggerMessage(
    EventId = 16,
    Level = LogLevel.Error,
    Message = "Error processing message {MessageId}"
  )]
  static partial void LogErrorProcessingMessage(ILogger logger, MessageId messageId, Exception ex);

  [LoggerMessage(
    EventId = 17,
    Level = LogLevel.Error,
    Message = "Could not resolve JsonTypeInfo for message type {MessageType} for message {MessageId}"
  )]
  static partial void LogCouldNotResolveJsonTypeInfo(ILogger logger, string messageType, Guid messageId);

  [LoggerMessage(
    EventId = 18,
    Level = LogLevel.Error,
    Message = "Failed to deserialize event payload from envelope for message {MessageId}"
  )]
  static partial void LogFailedToDeserializeEvent(ILogger logger, Guid messageId, Exception ex);

  [LoggerMessage(
    EventId = 19,
    Level = LogLevel.Information,
    Message = "ServiceBusConsumerWorker stopping..."
  )]
  static partial void LogWorkerStoppingGracefully(ILogger logger);

  [LoggerMessage(
    EventId = 20,
    Level = LogLevel.Debug,
    Message = "ServiceBus before FlushAsync: MessageId={MessageId}, IsEvent={IsEvent}, StreamId={StreamId}"
  )]
  static partial void LogBeforeFlush(ILogger logger, Guid messageId, bool isEvent, Guid? streamId);

  [LoggerMessage(
    EventId = 21,
    Level = LogLevel.Debug,
    Message = "ServiceBus after FlushAsync: TotalInboxWork={InboxWorkCount}, TotalOutboxWork={OutboxWorkCount}, TotalPerspectiveWork={PerspectiveWorkCount}"
  )]
  static partial void LogAfterFlush(ILogger logger, int inboxWorkCount, int outboxWorkCount, int perspectiveWorkCount);

  [LoggerMessage(
    EventId = 22,
    Level = LogLevel.Debug,
    Message = "ServiceBus work returned for MessageId={MessageId}: InboxWork={InboxCount}, IsEvent={IsEvent}"
  )]
  static partial void LogWorkReturned(ILogger logger, Guid messageId, int inboxCount, bool isEvent);

  [LoggerMessage(
    EventId = 23,
    Level = LogLevel.Debug,
    Message = "ServiceBus created InboxMessage: MessageId={MessageId}, IsEvent={IsEvent}, StreamId={StreamId}, MessageType={MessageType}, EnvelopeType={EnvelopeType}, PayloadType={PayloadType}"
  )]
  static partial void LogCreatedInboxMessage(ILogger logger, Guid messageId, bool isEvent, Guid? streamId, string messageType, string? envelopeType, JsonValueKind payloadType);

  [LoggerMessage(
    EventId = 24,
    Level = LogLevel.Error,
    Message = "Detached lifecycle stage {Stage} failed for message {MessageId}"
  )]
  private static partial void LogDetachedStageError(ILogger logger, Exception ex, LifecycleStage stage, Guid? messageId);

  [LoggerMessage(
    EventId = 25,
    Level = LogLevel.Debug,
    Message = "ServiceBus dropped message {MessageId} of unsubscribed type {EnvelopeType} — no consumer registered on this service"
  )]
  static partial void LogDroppedUnsubscribedType(ILogger logger, global::Whizbang.Core.ValueObjects.MessageId messageId, string envelopeType);
}

/// <summary>
/// Configuration options for ServiceBusConsumerWorker.
/// </summary>
/// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
/// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
public class ServiceBusConsumerOptions {
  /// <summary>
  /// List of topic subscriptions to consume messages from.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  public List<TopicSubscription> Subscriptions { get; set; } = [];
}

/// <summary>
/// Configuration for a single topic subscription.
/// </summary>
/// <param name="TopicName">The Service Bus topic name</param>
/// <param name="SubscriptionName">The subscription name for this consumer</param>
/// <param name="DestinationFilter">Optional destination filter value (e.g., "inventory-service")</param>
/// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
/// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
public record TopicSubscription(string TopicName, string SubscriptionName, string? DestinationFilter = null);
