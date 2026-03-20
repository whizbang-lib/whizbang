using System;
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
/// Default implementation of IMessagePublishStrategy that publishes messages via ITransport.
/// AUTOMATICALLY routes commands to shared inbox topic to ensure delivery.
/// Events use their destination directly (already namespace topics).
/// </summary>
public partial class TransportPublishStrategy : IMessagePublishStrategy {
  private const string LOG_CATEGORY = "Whizbang.Core.Transport";

  private readonly ITransport _transport;
  private readonly ITransportReadinessCheck _readinessCheck;
  private readonly string _inboxTopic;
#pragma warning disable S4487 // Used by generated [LoggerMessage] partial methods
  private readonly ILogger _logger;
#pragma warning restore S4487

  [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping transport for event-store-only message: {MessageType}")]
  private partial void LogSkippingEventStoreOnly(string messageType);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Publishing message: MessageType={MessageType}, OutboxDestination={OutboxDestination}, ResolvedAddress={ResolvedAddress}, ResolvedRoutingKey={ResolvedRoutingKey}")]
  private partial void LogPublishingMessage(string messageType, string outboxDestination, string resolvedAddress, string? resolvedRoutingKey);

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
  /// Creates a new TransportPublishStrategy with a custom inbox topic.
  /// Commands are automatically routed to the specified inbox topic.
  /// </summary>
  /// <param name="transport">The transport to publish messages to</param>
  /// <param name="readinessCheck">Readiness check to verify transport is ready before publishing</param>
  /// <param name="inboxTopic">The inbox topic name for commands (e.g., "whizbang" or "inbox")</param>
  public TransportPublishStrategy(ITransport transport, ITransportReadinessCheck readinessCheck, string inboxTopic, ILoggerFactory? loggerFactory = null) {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _readinessCheck = readinessCheck ?? throw new ArgumentNullException(nameof(readinessCheck));
    _inboxTopic = inboxTopic ?? throw new ArgumentNullException(nameof(inboxTopic));
    _logger = loggerFactory?.CreateLogger(LOG_CATEGORY) ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
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
    // Extract namespace and type name from assembly-qualified name
    var ns = _extractNamespace(typeFullName);
    var typeName = _extractTypeName(typeFullName);

    if (typeName is null) {
      return MessageKind.Unknown;
    }

    // Check namespace convention (Commands, Events, Queries)
    if (!string.IsNullOrEmpty(ns)) {
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
    }

    // Check type name suffix
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
}
