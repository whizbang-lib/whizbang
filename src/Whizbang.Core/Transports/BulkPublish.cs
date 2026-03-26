using System;
using System.Collections.Generic;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItem_WithRequiredProperties_SetsAllPropertiesAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItem_WithRoutingKey_SetsRoutingKeyAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItem_WithNullEnvelopeType_AllowsNullAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItem_RecordEquality_BehavesCorrectlyAsync</tests>
/// Represents a single item in a bulk publish operation.
/// Contains the message envelope, type metadata, and optional routing key.
/// All items in a single PublishBatchAsync call share the same TransportDestination address.
/// </summary>
public record BulkPublishItem {
  /// <summary>
  /// The message envelope to publish.
  /// </summary>
  public required IMessageEnvelope Envelope { get; init; }

  /// <summary>
  /// Optional assembly-qualified name of the envelope type.
  /// If provided, used instead of envelope.GetType() for serialization metadata.
  /// </summary>
  public required string? EnvelopeType { get; init; }

  /// <summary>
  /// The message ID for tracking and correlation.
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Optional per-item routing key. Overrides the TransportDestination.RoutingKey for this item.
  /// Used when items sharing the same destination address need different routing keys
  /// (e.g., different event types published to the same topic).
  /// </summary>
  public string? RoutingKey { get; init; }
}

/// <summary>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItemResult_Success_SetsPropertiesAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItemResult_Failure_SetsErrorAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItemResult_RecordEquality_BehavesCorrectlyAsync</tests>
/// Result of a single item in a bulk publish operation.
/// Enables per-item success/failure tracking for partial batch failures.
/// </summary>
public record BulkPublishItemResult {
  /// <summary>
  /// The message ID that was published (or attempted).
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Whether this specific item was successfully published.
  /// </summary>
  public required bool Success { get; init; }

  /// <summary>
  /// Error message if Success is false. Null if successful.
  /// </summary>
  public string? Error { get; init; }
}
