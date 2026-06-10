using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:Constructor_NullTransport_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:Constructor_NullReadinessCheck_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:IsReadyAsync_DefaultReadinessCheck_ReturnsTrueAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_SuccessfulPublish_ShouldReturnSuccessResultAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_TransportFailure_ShouldReturnFailureResultAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_WithNullScope_ShouldPublishSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_WithStreamId_ShouldIncludeInEnvelopeAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_WithRoutingStrategy_CommandRoutedToInboxAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_WithoutRoutingStrategy_CommandStillRoutedToInboxAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_WithStreamId_AddsStreamIdToDestinationMetadataAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_WithNullStreamId_DoesNotAddStreamIdToMetadataAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_WithStreamId_PopulatesStreamIdOnBulkPublishItemAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_SameAddressDifferentStreams_GroupsByStreamIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_SameAddressSameStream_KeepsInSingleBatchAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_MixedNullAndNonNullStreamIds_GroupsCorrectlyAsync</tests>
/// Default implementation of IMessagePublishStrategy that publishes messages via ITransport.
/// AUTOMATICALLY routes commands to shared inbox topic to ensure delivery.
/// Events use their destination directly (already namespace topics).
/// </summary>
/// <remarks>
/// Creates a new TransportPublishStrategy with a custom inbox topic.
/// Commands are automatically routed to the specified inbox topic.
/// </remarks>
/// <param name="transport">The transport to publish messages to</param>
/// <param name="readinessCheck">Readiness check to verify transport is ready before publishing</param>
/// <param name="inboxTopic">The inbox topic name for commands (e.g., "whizbang" or "inbox")</param>
public partial class TransportPublishStrategy(
  ITransport transport,
  ITransportReadinessCheck readinessCheck,
  string inboxTopic,
  ILoggerFactory? loggerFactory = null,
  ThrottleRetryOptions? throttleRetryOptions = null,
  TransportMetrics? metrics = null,
  Whizbang.Core.Offloads.PostSerializeHookChain? postSerializeHookChain = null,
  System.Text.Json.JsonSerializerOptions? jsonOptions = null
) : IMessagePublishStrategy {
  private const string LOG_CATEGORY = "Whizbang.Core.Transport";

  /// <summary>Destination metadata key carrying the on-wire envelope size in bytes — stamped by the publish strategy whenever serialization happens, regardless of offload outcome.</summary>
#pragma warning disable CA1707
  public const string BODY_SIZE_METADATA_KEY = "whizbang.body-size";
#pragma warning restore CA1707

  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  private readonly ITransportReadinessCheck _readinessCheck = readinessCheck ?? throw new ArgumentNullException(nameof(readinessCheck));
  private readonly string _inboxTopic = inboxTopic ?? throw new ArgumentNullException(nameof(inboxTopic));
  private readonly Whizbang.Core.Offloads.PostSerializeHookChain? _hookChain = postSerializeHookChain;
  private readonly System.Text.Json.JsonSerializerOptions? _jsonOptions = jsonOptions;
#pragma warning disable S4487 // Used by generated [LoggerMessage] partial methods
  private readonly ILogger _logger = loggerFactory?.CreateLogger(LOG_CATEGORY) ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
#pragma warning restore S4487
  private readonly ThrottleRetryOptions _throttleRetry = throttleRetryOptions ?? new ThrottleRetryOptions();
  private readonly TransportMetrics? _metrics = metrics;

  // Derived once: short transport tag for OTEL dimensions. Maps "AzureServiceBusTransport"
  // → "asb", "RabbitMQTransport" → "rabbitmq", etc. Unknown transport types fall back to the
  // class name so the dashboard still shows *something* distinguishable.
  private string _transportTag => _transport.GetType().Name switch {
    "AzureServiceBusTransport" => "asb",
    "RabbitMQTransport" => "rabbitmq",
    "InMemoryTransport" => "inmemory",
    var name => name,
  };

  [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping transport for event-store-only message: {MessageType}")]
  private partial void LogSkippingEventStoreOnly(string messageType);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Publishing message: MessageType={MessageType}, OutboxDestination={OutboxDestination}, ResolvedAddress={ResolvedAddress}, ResolvedRoutingKey={ResolvedRoutingKey}")]
  private partial void LogPublishingMessage(string messageType, string outboxDestination, string resolvedAddress, string? resolvedRoutingKey);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Publishing batch of {Count} messages to {Address}")]
  private partial void LogPublishingBatch(int count, string address);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Batch publish failed for group {Address}: {Error}")]
  private partial void LogBatchPublishGroupFailed(string address, string error);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Broker throttle on publish ({Transport}) — message {MessageId} attempt {Attempt}/{MaxAttempts}; sleeping {DelayMs:F0}ms before retry")]
  private partial void LogThrottleRetry(Guid messageId, int attempt, int maxAttempts, double delayMs, string transport);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Broker throttle on batch publish ({Transport}) — batch of {Count} messages attempt {Attempt}/{MaxAttempts}; sleeping {DelayMs:F0}ms before retry")]
  private partial void LogThrottleRetryBatch(int count, int attempt, int maxAttempts, double delayMs, string transport);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Broker throttle budget exhausted ({Transport}) after {Attempts} attempts for message {MessageId} — returning Throttled to failure channel")]
  private partial void LogThrottleBudgetExhausted(Guid messageId, int attempts, string transport);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Broker throttle budget exhausted ({Transport}) after {Attempts} attempts for batch of {Count} — returning Throttled to failure channel")]
  private partial void LogThrottleBudgetExhaustedBatch(int count, int attempts, string transport);

  /// <summary>
  /// Creates a new TransportPublishStrategy with default inbox topic.
  /// Commands are automatically routed to shared inbox topic.
  /// </summary>
  /// <param name="transport">The transport to publish messages to</param>
  /// <param name="readinessCheck">Readiness check to verify transport is ready before publishing</param>
  public TransportPublishStrategy(ITransport transport, ITransportReadinessCheck readinessCheck, ILoggerFactory? loggerFactory = null)
    : this(transport, readinessCheck, SharedTopicOutboxStrategy.DefaultInboxTopic, loggerFactory) {
  }

  /// <summary>
  /// Checks if the transport is ready to accept messages by delegating to the readiness check.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>True if transport is ready, false otherwise</returns>
  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    return _readinessCheck.IsReadyAsync(cancellationToken);
  }

  /// <summary>
  /// Publishes a single outbox message to the configured transport.
  /// Envelope is already deserialized - publishes directly via ITransport.
  /// When routing strategy is available, transforms the destination using the strategy.
  /// </summary>
  /// <param name="work">The outbox work item containing the message to publish</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Result indicating success/failure and any error details</returns>
  /// <remarks>
  /// <para>
  /// When <paramref name="work"/> has a null/empty destination, the message is an event-store-only
  /// message (from Route.Local or Route.EventStoreOnly). These messages are stored in the event store
  /// via process_work_batch but should not be transported. Returns success immediately.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/dispatcher/dispatcher#event-store-only</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_WithNullDestination_*</tests>
  public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
    // Skip transport publishing for event-store-only messages (destination is null)
    // These messages are stored in event store via process_work_batch but should not be transported
    if (string.IsNullOrEmpty(work.Destination)) {
      LogSkippingEventStoreOnly(work.MessageType);
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,  // Mark as published (processed)
        Error = null
      };
    }

    // Resolve transport destination (constant across retries within a single publish call)
    // PRIMARY: Uses destination from outbox (set correctly by Dispatcher when IOutboxRoutingStrategy is configured)
    // FALLBACK: Applies routing transformation for messages stored before routing was properly configured
    var destination = _resolveDestination(work);

    // Carry StreamId in destination metadata for transport-level FIFO ordering
    // Transports that support ordering (e.g., ASB sessions) use this to set SessionId
    if (work.StreamId.HasValue) {
      destination = _addStreamIdToMetadata(destination, work.StreamId.Value);
    }

    // Pre-serialize + post-serialize hook chain integration.
    // Only fires when something needs the size before the transport gets it:
    //   - transport reports a wire ceiling (so we have to validate pre-flight), OR
    //   - at least one hook is registered (size measurement, body offload, etc.)
    // When neither applies (e.g., InProcessTransport with no hooks), skip the
    // serialization cost entirely — preserves the existing fast path.
    Whizbang.Core.Observability.IMessageEnvelope envelopeToPublish = work.Envelope;
    var envelopeTypeToPublish = work.EnvelopeType;
    ReadOnlyMemory<byte>? preSerializedBytes = null;

    var needsPreSerialize = _hookChain is not null
                            && _jsonOptions is not null
                            && (!_hookChain.IsEmpty || _transport.MaxMessageSizeBytes is not null);
    if (needsPreSerialize) {
      var preflight = await _runPostSerializeChainAsync(work, destination, cancellationToken);
      if (preflight.Failure is { } failure) {
        return failure;
      }
      envelopeToPublish = preflight.Envelope!;
      envelopeTypeToPublish = preflight.EnvelopeType;
      preSerializedBytes = preflight.Bytes;
      destination = preflight.Destination!;
    }

    // In-memory retry loop on broker-side throttling. Lease is already held; the broker
    // pause is typically sub-second to a few seconds; releasing the lease and waiting for
    // claim_orphaned_outbox would prematurely burn down the dead-letter budget AND add
    // tens of seconds of latency on transient pressure. See ThrottleRetryOptions docs.
    var attempt = 1;
    while (true) {
      try {
        LogPublishingMessage(work.MessageType, work.Destination, destination.Address, destination.RoutingKey);

        // Publish to transport - envelope is already deserialized
        // OutboxWork is non-generic, Envelope is IMessageEnvelope<object>
        // Pass EnvelopeType from OutboxWork to preserve original payload type information.
        // When the hook chain ran, the bytes hint carries whatever the chain produced
        // (potentially a substitute claim envelope when offload triggered).
        await _transport.PublishAsync(envelopeToPublish, destination, envelopeTypeToPublish, preSerializedBytes: preSerializedBytes, cancellationToken);

        return new MessagePublishResult {
          MessageId = work.MessageId,
          Success = true,
          CompletedStatus = MessageProcessingStatus.Published,
          Error = null
        };
      } catch (Exception ex) {
        var reason = TransportFailureClassifier.Classify(ex);
        if (reason == MessageFailureReason.Throttled && attempt < _throttleRetry.MaxAttempts) {
          _metrics?.OutboxPublishThrottled.Add(1, new KeyValuePair<string, object?>("transport", _transportTag));
          var delay = _throttleRetry.ComputeDelay(attempt);
          LogThrottleRetry(work.MessageId, attempt, _throttleRetry.MaxAttempts, delay.TotalMilliseconds, _transportTag);
          await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
          attempt++;
          continue;
        }

        // Either non-throttle failure OR throttle budget exhausted. Return failure with the
        // classified reason so the worker / failure channel / dashboards can distinguish
        // broker throttling (raise tier) from outright outages (fix the outage).
        if (reason == MessageFailureReason.Throttled) {
          LogThrottleBudgetExhausted(work.MessageId, attempt, _transportTag);
        }
        return new MessagePublishResult {
          MessageId = work.MessageId,
          Success = false,
          CompletedStatus = work.Status, // Already stored, publish failed
          Error = $"{ex.GetType().Name}: {ex.Message}",
          Reason = reason,
        };
      }
    }
  }

  /// <summary>
  /// Whether this strategy supports bulk publishing.
  /// Returns true when the underlying transport declares the BulkPublish capability.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:SupportsBulkPublish_WithBulkCapableTransport_ReturnsTrueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:SupportsBulkPublish_WithoutBulkCapableTransport_ReturnsFalseAsync</tests>
  public bool SupportsBulkPublish =>
    _transport.Capabilities.HasFlag(TransportCapabilities.BulkPublish);

  /// <summary>
  /// Publishes a batch of outbox messages to the configured transport.
  /// Groups messages by resolved destination address and calls PublishBatchAsync per group.
  /// Event-store-only messages (null/empty destination) are returned as immediate successes.
  /// Partial failures are handled per-group: if a group's transport call throws, all items in that group fail.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_SingleDestination_CallsTransportOnceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_MultipleDestinations_GroupsByAddressAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_EventStoreOnlyItems_ReturnSuccessWithoutCallingTransportAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_TransportThrowsForGroup_FailsOnlyThatGroupAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_PerItemRoutingKeys_SetCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_EmptyList_ReturnsEmptyResultsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_PartialItemResults_MapsCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishBatchAsync_AllEventStoreOnly_NoTransportCallsAsync</tests>
  public async Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(
    IReadOnlyList<OutboxWork> workItems,
    CancellationToken cancellationToken) {
    if (workItems.Count == 0) {
      return [];
    }

    var results = new List<MessagePublishResult>(workItems.Count);

    // Separate event-store-only items (null/empty destination) from transportable items
    var transportableItems = new List<(OutboxWork Work, TransportDestination Destination)>();

    foreach (var work in workItems) {
      if (string.IsNullOrEmpty(work.Destination)) {
        LogSkippingEventStoreOnly(work.MessageType);
        results.Add(new MessagePublishResult {
          MessageId = work.MessageId,
          Success = true,
          CompletedStatus = MessageProcessingStatus.Published,
          Error = null
        });
      } else {
        var destination = _resolveDestination(work);
        transportableItems.Add((work, destination));
      }
    }

    // Group by (destination address, stream ID) for batch transport calls.
    // Messages with the same StreamId stay together to preserve FIFO ordering.
    // Different StreamIds get separate batch calls so transports can handle sessions correctly.
    var groups = transportableItems.GroupBy(item => (item.Destination.Address, item.Work.StreamId));

    foreach (var group in groups) {
      var groupItems = group.ToList();
      // Use the first item's destination as the shared destination (address is the same for all)
      var sharedDestination = groupItems[0].Destination;

      // Build BulkPublishItems with per-item routing keys
      var bulkItems = new List<BulkPublishItem>(groupItems.Count);
      foreach (var (work, destination) in groupItems) {
        bulkItems.Add(new BulkPublishItem {
          Envelope = work.Envelope,
          EnvelopeType = work.EnvelopeType,
          MessageId = work.MessageId,
          RoutingKey = destination.RoutingKey,
          StreamId = work.StreamId
        });
      }

      // In-memory throttle retry for the whole batch group. Broker throttling on bulk send
      // is namespace-wide (not per-item), so retrying the whole group is correct. We retry
      // either when the batch CALL itself throws a throttle exception OR when every returned
      // item carries Reason=Throttled. Mixed-success batches are not retried — they reflect
      // per-message conditions, not a uniform broker pause.
      var batchAttempt = 1;
      while (true) {
        Exception? caught = null;
        IReadOnlyList<BulkPublishItemResult>? batchResults = null;
        try {
          LogPublishingBatch(bulkItems.Count, sharedDestination.Address);
          batchResults = await _transport.PublishBatchAsync(bulkItems, sharedDestination, cancellationToken);
        } catch (Exception ex) {
          caught = ex;
        }

        var batchThrottled = caught is not null
          ? TransportFailureClassifier.Classify(caught) == MessageFailureReason.Throttled
          : batchResults!.All(r => !r.Success
              && TransportFailureClassifier.Classify(new InvalidOperationException(r.Error ?? "")) == MessageFailureReason.Throttled);

        if (batchThrottled && batchAttempt < _throttleRetry.MaxAttempts) {
          _metrics?.OutboxPublishThrottled.Add(bulkItems.Count, new KeyValuePair<string, object?>("transport", _transportTag));
          var delay = _throttleRetry.ComputeDelay(batchAttempt);
          LogThrottleRetryBatch(bulkItems.Count, batchAttempt, _throttleRetry.MaxAttempts, delay.TotalMilliseconds, _transportTag);
          await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
          batchAttempt++;
          continue;
        }

        if (caught is not null) {
          LogBatchPublishGroupFailed(sharedDestination.Address, caught.Message);
          var reason = TransportFailureClassifier.Classify(caught);
          if (reason == MessageFailureReason.Throttled) {
            LogThrottleBudgetExhaustedBatch(bulkItems.Count, batchAttempt, _transportTag);
          }
          foreach (var (work, _) in groupItems) {
            results.Add(new MessagePublishResult {
              MessageId = work.MessageId,
              Success = false,
              CompletedStatus = work.Status,
              Error = $"{caught.GetType().Name}: {caught.Message}",
              Reason = reason,
            });
          }
        } else {
          // Happy/mixed path — map per-item results.
          foreach (var batchResult in batchResults!) {
            var originalWork = groupItems.First(g => g.Work.MessageId == batchResult.MessageId).Work;
            results.Add(new MessagePublishResult {
              MessageId = batchResult.MessageId,
              Success = batchResult.Success,
              CompletedStatus = batchResult.Success ? MessageProcessingStatus.Published : originalWork.Status,
              Error = batchResult.Error
            });
          }
        }
        break;
      }
    }

    return results;
  }

  /// <summary>
  /// Resolves the actual transport destination for a message.
  /// ALWAYS routes commands to shared inbox topic - this is critical for message delivery.
  /// Events use their destination directly (already namespace topics).
  /// </summary>
  /// <param name="work">The outbox work item</param>
  /// <returns>The resolved transport destination</returns>
  private TransportDestination _resolveDestination(OutboxWork work) {
    // ALWAYS detect message kind - commands MUST go to inbox, not individual command topics
    // This is critical: without this, commands would be published to non-existent topics
    // and silently dropped by the message broker
    var messageKind = _detectMessageKindFromTypeName(work.MessageType);

    // For commands, ALWAYS route to shared inbox topic
    // This applies whether or not WithRouting() was explicitly called
    if (messageKind == MessageKind.Command) {
      // Commands go to shared inbox topic (configured via constructor)
      // Parse the type name to get the routing key for filtering
      var typeName = _extractTypeName(work.MessageType)?.ToLowerInvariant() ?? work.Destination;
      var ns = _extractNamespace(work.MessageType)?.ToLowerInvariant() ?? "";
      var routingKey = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";

      return new TransportDestination(
        Address: _inboxTopic,
        RoutingKey: routingKey
      );
    }

    // Events use destination directly (already resolved to namespace topic)
    // Null check should never trigger due to early return in PublishAsync, but be defensive
    if (string.IsNullOrEmpty(work.Destination)) {
      throw new InvalidOperationException(
        "Event destination cannot be null or empty at this point. " +
        "Event-store-only messages should be handled by early return in PublishAsync. " +
        $"MessageId: {work.MessageId}, MessageType: {work.MessageType}");
    }

    // IMPORTANT: For events, set RoutingKey to the event's namespace path (e.g., "myapp.users.UserCreated")
    // This is used as the Subject property in Azure Service Bus for SqlFilter matching.
    // Without this, the Subject defaults to "message" and SqlFilter patterns like "[Subject] LIKE 'myapp.users.%'" won't match.
    var eventTypeName = _extractTypeName(work.MessageType)?.ToLowerInvariant() ?? "";
    var eventNamespace = _extractNamespace(work.MessageType)?.ToLowerInvariant() ?? "";

    // Build full routing key: namespace.typename (e.g., "myapp.users.events.usercreated")
    var eventRoutingKey = string.IsNullOrEmpty(eventNamespace)
      ? eventTypeName
      : $"{eventNamespace}.{eventTypeName}";

    return new TransportDestination(
      Address: work.Destination,
      RoutingKey: eventRoutingKey
    );
  }

  /// <summary>
  /// Detects MessageKind from assembly-qualified type name string (AOT-safe).
  /// Uses namespace and type name conventions without loading the Type.
  /// </summary>
  /// <param name="typeFullName">Assembly-qualified type name (e.g., "MyApp.Commands.CreateTenantCommand, MyApp")</param>
  /// <returns>Detected MessageKind or Unknown</returns>
  private static MessageKind _detectMessageKindFromTypeName(string typeFullName) {
    var ns = _extractNamespace(typeFullName);
    var typeName = _extractTypeName(typeFullName);

    if (typeName is null) {
      return MessageKind.Unknown;
    }

    var kindFromNamespace = _detectMessageKindFromNamespace(ns);
    if (kindFromNamespace != MessageKind.Unknown) {
      return kindFromNamespace;
    }

    return _detectMessageKindFromSuffix(typeName);
  }

  /// <summary>
  /// Detects MessageKind from namespace segments (e.g., "MyApp.Commands" returns Command).
  /// </summary>
  private static MessageKind _detectMessageKindFromNamespace(string? ns) {
    if (string.IsNullOrEmpty(ns)) {
      return MessageKind.Unknown;
    }

    var segments = ns.Split('.');
    foreach (var segment in segments) {
      if (string.Equals(segment, "Commands", StringComparison.OrdinalIgnoreCase)) {
        return MessageKind.Command;
      }
      if (string.Equals(segment, "Events", StringComparison.OrdinalIgnoreCase)) {
        return MessageKind.Event;
      }
      if (string.Equals(segment, "Queries", StringComparison.OrdinalIgnoreCase)) {
        return MessageKind.Query;
      }
    }

    return MessageKind.Unknown;
  }

  /// <summary>
  /// Detects MessageKind from type name suffix (e.g., "CreateTenantCommand" returns Command).
  /// </summary>
  private static MessageKind _detectMessageKindFromSuffix(string typeName) {
    if (typeName.EndsWith("Command", StringComparison.Ordinal)) {
      return MessageKind.Command;
    }
    if (typeName.EndsWith("Query", StringComparison.Ordinal)) {
      return MessageKind.Query;
    }
    if (typeName.EndsWith("Event", StringComparison.Ordinal) ||
        typeName.EndsWith("Created", StringComparison.Ordinal) ||
        typeName.EndsWith("Updated", StringComparison.Ordinal) ||
        typeName.EndsWith("Deleted", StringComparison.Ordinal)) {
      return MessageKind.Event;
    }

    return MessageKind.Unknown;
  }

  private static string? _extractNamespace(string typeFullName) =>
    TypeNameFormatter.GetNamespace(typeFullName);

  private static string? _extractTypeName(string typeFullName) =>
    TypeNameFormatter.GetSimpleName(typeFullName);

  /// <summary>
  /// Creates a new TransportDestination with StreamId added to the metadata dictionary.
  /// Used to carry stream ordering context to transports that support FIFO (e.g., ASB sessions).
  /// </summary>
  private static TransportDestination _addStreamIdToMetadata(TransportDestination destination, Guid streamId) {
    var metadata = new Dictionary<string, JsonElement>();

    // Preserve existing metadata entries
    if (destination.Metadata is not null) {
      foreach (var kvp in destination.Metadata) {
        metadata[kvp.Key] = kvp.Value;
      }
    }

    metadata["StreamId"] = JsonDocument.Parse($"\"{streamId}\"").RootElement;

    return destination with { Metadata = metadata };
  }

  /// <summary>
  /// Pre-flight: serialize the envelope once, run the post-serialize hook
  /// chain (size measurement, body offload, etc.), stamp
  /// <c>whizbang.body-size</c> on destination metadata, and validate that
  /// the final wire bytes fit the transport's ceiling. Returns either the
  /// chain's outcome (envelope/type/bytes/destination) for the publish
  /// step or a pre-flight failure result the caller returns directly.
  /// </summary>
  private async Task<_postSerializeOutcome> _runPostSerializeChainAsync(
      OutboxWork work,
      TransportDestination destination,
      CancellationToken cancellationToken) {
    var runtimeType = work.Envelope.GetType();
    var typeInfo = _jsonOptions!.GetTypeInfo(runtimeType)
      ?? throw new InvalidOperationException(
        $"No JsonTypeInfo found for {runtimeType.Name}. Register MessageEnvelope<T> via JsonContextRegistry before enabling post-serialize hooks.");

    var json = JsonSerializer.Serialize(work.Envelope, typeInfo);
    var originalBytes = System.Text.Encoding.UTF8.GetBytes(json);

    var envelopeTypeName = work.EnvelopeType
      ?? runtimeType.AssemblyQualifiedName
      ?? throw new InvalidOperationException("Envelope type must have an assembly-qualified name.");

    var context = new Whizbang.Core.Offloads.PostSerializeContext(
      Envelope: work.Envelope,
      EnvelopeType: envelopeTypeName,
      SerializedBytes: originalBytes,
      ContentType: "application/json",
      TransportMaxMessageSizeBytes: _transport.MaxMessageSizeBytes,
      JsonOptions: _jsonOptions,
      Destination: destination);

    var outcome = await _hookChain!.RunAsync(context, cancellationToken);

    // Stamp whizbang.body-size based on the FINAL bytes (post-hook chain).
    var finalDestinationMetadata = new Dictionary<string, JsonElement>();
    if (outcome.MergedDestinationMetadata is not null) {
      foreach (var (k, v) in outcome.MergedDestinationMetadata) {
        finalDestinationMetadata[k] = v;
      }
    }
    var sizeJson = outcome.FinalSerializedBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
    finalDestinationMetadata[BODY_SIZE_METADATA_KEY] = JsonDocument.Parse(sizeJson).RootElement;

    var finalDestination = destination with { Metadata = finalDestinationMetadata };

    // Pre-flight ceiling check. If the chain didn't substitute the body and the
    // size still exceeds the transport's ceiling, fail BEFORE handing it to the
    // transport — the outbox row stays put with a clear reason code.
    if (_transport.MaxMessageSizeBytes is long max && outcome.FinalSerializedBytes.Length > max) {
      return new _postSerializeOutcome {
        Failure = new MessagePublishResult {
          MessageId = work.MessageId,
          Success = false,
          CompletedStatus = work.Status,
          Error = $"Serialized envelope is {outcome.FinalSerializedBytes.Length} bytes; transport ceiling is {max} bytes. Register a body-offload provider (services.AddWhizbangBodyOffload() + AddWhizbang*Offload(name) + Configure<MessageBodyOffloadOptions>(opts => opts.ProviderName = name)), raise the transport tier, or trim the payload.",
          Reason = MessageFailureReason.MessageBodyTooLarge,
        }
      };
    }

    return new _postSerializeOutcome {
      Envelope = outcome.FinalEnvelope,
      EnvelopeType = outcome.FinalEnvelopeType,
      Bytes = outcome.FinalSerializedBytes,
      Destination = finalDestination,
    };
  }

  /// <summary>Internal carrier from <see cref="_runPostSerializeChainAsync"/>. Either Failure is set (pre-flight rejection) or the four chain-outcome fields are.</summary>
  private sealed class _postSerializeOutcome {
    public Whizbang.Core.Observability.IMessageEnvelope? Envelope { get; init; }
    public string? EnvelopeType { get; init; }
    public ReadOnlyMemory<byte> Bytes { get; init; }
    public TransportDestination? Destination { get; init; }
    public MessagePublishResult? Failure { get; init; }
  }
}
