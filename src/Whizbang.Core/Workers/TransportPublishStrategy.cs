using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
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
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs:PublishAsync_WithRoutingStrategy_TransformsDestinationAsync</tests>
/// Default implementation of IMessagePublishStrategy that publishes messages via ITransport.
/// Publishes envelope objects directly to the configured transport.
/// When IOutboxRoutingStrategy is provided, transforms the destination using the routing strategy.
/// </summary>
public class TransportPublishStrategy : IMessagePublishStrategy {
  private readonly ITransport _transport;
  private readonly ITransportReadinessCheck _readinessCheck;
  private readonly IOutboxRoutingStrategy? _routingStrategy;
  private readonly IReadOnlySet<string>? _ownedDomains;

  /// <summary>
  /// Creates a new TransportPublishStrategy.
  /// </summary>
  /// <param name="transport">The transport to publish messages to</param>
  /// <param name="readinessCheck">Readiness check to verify transport is ready before publishing</param>
  public TransportPublishStrategy(ITransport transport, ITransportReadinessCheck readinessCheck) {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _readinessCheck = readinessCheck ?? throw new ArgumentNullException(nameof(readinessCheck));
  }

  /// <summary>
  /// Creates a new TransportPublishStrategy with routing support.
  /// </summary>
  /// <param name="transport">The transport to publish messages to</param>
  /// <param name="readinessCheck">Readiness check to verify transport is ready before publishing</param>
  /// <param name="routingStrategy">Routing strategy to transform destinations</param>
  /// <param name="routingOptions">Routing options containing owned domains</param>
  public TransportPublishStrategy(
    ITransport transport,
    ITransportReadinessCheck readinessCheck,
    IOutboxRoutingStrategy? routingStrategy,
    IOptions<RoutingOptions>? routingOptions) {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _readinessCheck = readinessCheck ?? throw new ArgumentNullException(nameof(readinessCheck));
    _routingStrategy = routingStrategy;
    _ownedDomains = routingOptions?.Value.OwnedDomains;
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
  public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
    try {
      // Resolve transport destination - use routing strategy if available
      var destination = _resolveDestination(work);

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
  /// If routing strategy is configured, transforms command destinations to shared inbox.
  /// Otherwise, uses the destination directly from the outbox.
  /// </summary>
  /// <param name="work">The outbox work item</param>
  /// <returns>The resolved transport destination</returns>
  private TransportDestination _resolveDestination(OutboxWork work) {
    // If no routing strategy is configured, use destination directly
    if (_routingStrategy is null || _ownedDomains is null) {
      return new TransportDestination(work.Destination);
    }

    // Detect message kind from type name (AOT-safe, no Type.GetType)
    var messageKind = _detectMessageKindFromTypeName(work.MessageType);

    // For commands, route to shared inbox
    // For events, use the destination directly (already the namespace topic)
    if (messageKind == MessageKind.Command) {
      // Commands go to shared inbox topic
      // Parse the type name to get the routing key for filtering
      var typeName = _extractTypeName(work.MessageType)?.ToLowerInvariant() ?? work.Destination;
      var ns = _extractNamespace(work.MessageType)?.ToLowerInvariant() ?? "";
      var routingKey = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";

      return new TransportDestination(
        Address: SharedTopicOutboxStrategy.DefaultInboxTopic,
        RoutingKey: routingKey
      );
    }

    // Events use destination directly (already resolved to namespace topic)
    return new TransportDestination(work.Destination);
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

  /// <summary>
  /// Extracts the namespace from an assembly-qualified type name.
  /// </summary>
  /// <param name="typeFullName">Assembly-qualified type name</param>
  /// <returns>Namespace or null</returns>
  private static string? _extractNamespace(string typeFullName) {
    // Format: "Namespace.TypeName, AssemblyName" or "Namespace.TypeName"
    var commaIndex = typeFullName.IndexOf(',');
    var fullTypeName = commaIndex >= 0 ? typeFullName[..commaIndex].Trim() : typeFullName.Trim();

    var lastDotIndex = fullTypeName.LastIndexOf('.');
    if (lastDotIndex < 0) {
      return null;
    }

    return fullTypeName[..lastDotIndex];
  }

  /// <summary>
  /// Extracts the type name from an assembly-qualified type name.
  /// </summary>
  /// <param name="typeFullName">Assembly-qualified type name</param>
  /// <returns>Type name or null</returns>
  private static string? _extractTypeName(string typeFullName) {
    // Format: "Namespace.TypeName, AssemblyName" or "Namespace.TypeName"
    var commaIndex = typeFullName.IndexOf(',');
    var fullTypeName = commaIndex >= 0 ? typeFullName[..commaIndex].Trim() : typeFullName.Trim();

    var lastDotIndex = fullTypeName.LastIndexOf('.');
    if (lastDotIndex < 0) {
      return fullTypeName;
    }

    return fullTypeName[(lastDotIndex + 1)..];
  }
}

