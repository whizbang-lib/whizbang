using System;
using System.Collections.Generic;
using System.Text.Json;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// Represents a single item in a bulk publish operation.
/// Contains the message envelope, type metadata, and optional routing key.
/// All items in a single PublishBatchAsync call share the same TransportDestination address.
/// </summary>
/// <docs>messaging/transports/transports</docs>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItem_WithRoutingKey_SetsRoutingKeyAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItem_WithNullEnvelopeType_AllowsNullAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItem_WithRequiredProperties_SetsAllPropertiesAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItem_WithStreamId_SetsStreamIdAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItem_RecordEquality_BehavesCorrectlyAsync</tests>
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

  /// <summary>
  /// Optional stream ID for FIFO ordering. When set, transports that support ordering
  /// (e.g., Azure Service Bus sessions) use this to guarantee message delivery order
  /// within the stream. Messages with the same StreamId are delivered in MessageId order.
  /// </summary>
  /// <docs>messaging/transports/transports</docs>
  /// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItem_WithStreamId_SetsStreamIdAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItem_WithoutStreamId_DefaultsToNullAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItem_RecordEquality_WithStreamId_BehavesCorrectlyAsync</tests>
  public Guid? StreamId { get; init; }

  /// <summary>
  /// Optional pre-serialized envelope bytes. When set, transports MUST
  /// use these bytes on the wire and SKIP their internal serialization.
  /// Set by upstream callers (e.g., <c>TransportPublishStrategy</c>) that
  /// already serialized the envelope once — typically for size measurement
  /// + post-serialize hook chain — so the transport doesn't re-do the work.
  /// When null, the transport serializes normally.
  /// </summary>
  /// <remarks>
  /// In-process / object-pass-through transports (e.g.,
  /// <c>InProcessTransport</c>) MAY ignore this hint, as they don't
  /// touch bytes; wire transports (RabbitMQ, Azure Service Bus) MUST
  /// honor it when present.
  /// </remarks>
  public ReadOnlyMemory<byte>? PreSerializedBytes { get; init; }

  /// <summary>
  /// Optional per-item metadata applied to THIS item's wire-message
  /// ApplicationProperties / Headers in addition to the shared
  /// <see cref="TransportDestination.Metadata"/>. Used for properties
  /// that vary per item — e.g., <c>whizbang.body-size</c> stamped by the
  /// post-serialize hook chain.
  /// </summary>
  /// <remarks>
  /// Keys collide-by-overwrite: per-item entries override shared
  /// destination metadata. This makes per-item overrides "win" without
  /// transport-specific merge logic.
  /// </remarks>
  public IReadOnlyDictionary<string, JsonElement>? PerItemMetadata { get; init; }
}

/// <summary>
/// Result of a single item in a bulk publish operation.
/// Enables per-item success/failure tracking for partial batch failures.
/// </summary>
/// <docs>messaging/transports/transports</docs>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItemResult_Success_SetsPropertiesAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItemResult_Failure_SetsErrorAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/BulkPublishTests.cs:BulkPublishItemResult_RecordEquality_BehavesCorrectlyAsync</tests>
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
