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
public partial class TransportPublishStrategy(ITransport transport, ITransportReadinessCheck readinessCheck, string inboxTopic, ILoggerFactory? loggerFactory = null) : IMessagePublishStrategy {
  private const string LOG_CATEGORY = "Whizbang.Core.Transport";

  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  private readonly ITransportReadinessCheck _readinessCheck = readinessCheck ?? throw new ArgumentNullException(nameof(readinessCheck));
  private readonly string _inboxTopic = inboxTopic ?? throw new ArgumentNullException(nameof(inboxTopic));
#pragma warning disable S4487 // Used by generated [LoggerMessage] partial methods
  private readonly ILogger _logger = loggerFactory?.CreateLogger(LOG_CATEGORY) ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
#pragma warning restore S4487

  [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping transport for event-store-only message: {MessageType}")]
  private partial void LogSkippingEventStoreOnly(string messageType);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Publishing message: MessageType={MessageType}, OutboxDestination={OutboxDestination}, ResolvedAddress={ResolvedAddress}, ResolvedRoutingKey={ResolvedRoutingKey}")]
  private partial void LogPublishingMessage(string messageType, string outboxDestination, string resolvedAddress, string? resolvedRoutingKey);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Publishing batch of {Count} messages to {Address}")]
  private partial void LogPublishingBatch(int count, string address);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Batch publish failed for group {Address}: {Error}")]
  private partial void LogBatchPublishGroupFailed(string address, string error);

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
    try {
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

      // Resolve transport destination
      // PRIMARY: Uses destination from outbox (set correctly by Dispatcher when IOutboxRoutingStrategy is configured)
      // FALLBACK: Applies routing transformation for messages stored before routing was properly configured
      var destination = _resolveDestination(work);

      // Carry StreamId in destination metadata for transport-level FIFO ordering
      // Transports that support ordering (e.g., ASB sessions) use this to set SessionId
      if (work.StreamId.HasValue) {
        destination = _addStreamIdToMetadata(destination, work.StreamId.Value);
      }

      LogPublishingMessage(work.MessageType, work.Destination, destination.Address, destination.RoutingKey);

      // Publish to transport - envelope is already deserialized
      // OutboxWork is non-generic, Envelope is IMessageEnvelope<object>
      // Pass EnvelopeType from OutboxWork to preserve original payload type information
      await _transport.PublishAsync(work.Envelope, destination, work.EnvelopeType, cancellationToken);

      // Return success result
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      };
    } catch (Exception ex) {
      // Return failure result with error details
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = false,
        CompletedStatus = work.Status, // Already stored, publish failed
        Error = $"{ex.GetType().Name}: {ex.Message}"
      };
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

      try {
        LogPublishingBatch(bulkItems.Count, sharedDestination.Address);
        var batchResults = await _transport.PublishBatchAsync(bulkItems, sharedDestination, cancellationToken);

        // Map transport results back to MessagePublishResult
        foreach (var batchResult in batchResults) {
          var originalWork = groupItems.First(g => g.Work.MessageId == batchResult.MessageId).Work;
          results.Add(new MessagePublishResult {
            MessageId = batchResult.MessageId,
            Success = batchResult.Success,
            CompletedStatus = batchResult.Success ? MessageProcessingStatus.Published : originalWork.Status,
            Error = batchResult.Error
          });
        }
      } catch (Exception ex) {
        LogBatchPublishGroupFailed(sharedDestination.Address, ex.Message);
        // All items in this group fail
        foreach (var (work, _) in groupItems) {
          results.Add(new MessagePublishResult {
            MessageId = work.MessageId,
            Success = false,
            CompletedStatus = work.Status,
            Error = $"{ex.GetType().Name}: {ex.Message}"
          });
        }
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
}
