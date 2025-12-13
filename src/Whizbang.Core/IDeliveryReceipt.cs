using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core;

/// <summary>
/// Represents a delivery receipt for a dispatched message.
/// Contains correlation information and delivery metadata, but NOT the business result.
/// </summary>
/// <docs>core-concepts/dispatcher</docs>
public interface IDeliveryReceipt {
  /// <summary>
  /// Unique identifier for this message
  /// </summary>
  MessageId MessageId { get; }

  /// <summary>
  /// When the message was accepted by the dispatcher
  /// </summary>
  DateTimeOffset Timestamp { get; }

  /// <summary>
  /// Where the message was routed (receptor name, topic, etc.)
  /// </summary>
  string Destination { get; }

  /// <summary>
  /// Current delivery status
  /// </summary>
  DeliveryStatus Status { get; }

  /// <summary>
  /// Extensible metadata for transport-specific information.
  /// Supports any JSON value type (string, number, boolean, object, array) via JsonElement.
  /// </summary>
  IReadOnlyDictionary<string, JsonElement> Metadata { get; }

  /// <summary>
  /// Correlation ID from the message context
  /// </summary>
  CorrelationId? CorrelationId { get; }

  /// <summary>
  /// Causation ID (ID of the message that caused this message)
  /// </summary>
  MessageId? CausationId { get; }
}

/// <summary>
/// Delivery status for a message
/// </summary>
public enum DeliveryStatus {
  /// <summary>
  /// Message accepted by dispatcher and ready for processing
  /// </summary>
  Accepted = 0,

  /// <summary>
  /// Message queued for async processing (e.g., via inbox pattern)
  /// </summary>
  Queued = 1,

  /// <summary>
  /// Message delivered to handler (handler executed)
  /// </summary>
  Delivered = 2,

  /// <summary>
  /// Message failed to deliver or process
  /// </summary>
  Failed = 3
}

/// <summary>
/// Concrete implementation of IDeliveryReceipt
/// </summary>
/// <remarks>
/// Creates a new delivery receipt
/// </remarks>
public sealed class DeliveryReceipt(
  MessageId messageId,
  string destination,
  DeliveryStatus status,
  CorrelationId? correlationId = null,
  MessageId? causationId = null,
  Dictionary<string, JsonElement>? metadata = null
  ) : IDeliveryReceipt {

  /// <inheritdoc />
  public MessageId MessageId { get; } = messageId;

  /// <inheritdoc />
  public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;

  /// <inheritdoc />
  public string Destination { get; } = destination ?? throw new ArgumentNullException(nameof(destination));

  /// <inheritdoc />
  public DeliveryStatus Status { get; } = status;

  /// <inheritdoc />
  public IReadOnlyDictionary<string, JsonElement> Metadata { get; } = metadata != null
        ? new Dictionary<string, JsonElement>(metadata)
        : [];

  /// <inheritdoc />
  public CorrelationId? CorrelationId { get; } = correlationId;

  /// <inheritdoc />
  public MessageId? CausationId { get; } = causationId;

  /// <summary>
  /// Creates a delivery receipt for an accepted message
  /// </summary>
  public static DeliveryReceipt Accepted(
    MessageId messageId,
    string destination,
    CorrelationId? correlationId = null,
    MessageId? causationId = null
  ) {
    return new DeliveryReceipt(
      messageId,
      destination,
      DeliveryStatus.Accepted,
      correlationId,
      causationId
    );
  }

  /// <summary>
  /// Creates a delivery receipt for a queued message
  /// </summary>
  public static DeliveryReceipt Queued(
    MessageId messageId,
    string destination,
    CorrelationId? correlationId = null,
    MessageId? causationId = null
  ) {
    return new DeliveryReceipt(
      messageId,
      destination,
      DeliveryStatus.Queued,
      correlationId,
      causationId
    );
  }

  /// <summary>
  /// Creates a delivery receipt for a delivered message
  /// </summary>
  public static DeliveryReceipt Delivered(
    MessageId messageId,
    string destination,
    CorrelationId? correlationId = null,
    MessageId? causationId = null
  ) {
    return new DeliveryReceipt(
      messageId,
      destination,
      DeliveryStatus.Delivered,
      correlationId,
      causationId
    );
  }

  /// <summary>
  /// Creates a delivery receipt for a failed message
  /// </summary>
  public static DeliveryReceipt Failed(
    MessageId messageId,
    string destination,
    CorrelationId? correlationId = null,
    MessageId? causationId = null,
    Exception? exception = null
  ) {
    var metadata = exception != null
        ? new Dictionary<string, JsonElement> {
          ["ExceptionType"] = JsonElementHelper.FromString(exception.GetType().FullName),
          ["ExceptionMessage"] = JsonElementHelper.FromString(exception.Message),
          ["ExceptionStackTrace"] = JsonElementHelper.FromString(exception.StackTrace)
        }
        : null;

    return new DeliveryReceipt(
      messageId,
      destination,
      DeliveryStatus.Failed,
      correlationId,
      causationId,
      metadata
    );
  }
}
