using System;

namespace Whizbang.Core.Transports;

/// <summary>
/// Everything a transport dead-letter drainer hands the coordinator to give a broker-dead-lettered
/// message durable custody in <c>wh_dead_letters</c>. Built entirely from broker message metadata
/// and the RAW wire body — importing never deserializes the envelope (a message that cannot be
/// deserialized is precisely the one that needs custody), so the import path is AOT-clean by
/// construction.
/// </summary>
/// <docs>operations/dead-letter-queue/transport-recovery</docs>
/// <param name="MessageId">The wire message id — Whizbang publishes envelope ids as the broker
///   MessageId, so this is the envelope's id and the import idempotency key.</param>
/// <param name="StreamId">The per-stream session key when present (ASB SessionId).</param>
/// <param name="MessageType">The wire envelope type name (the EnvelopeType application property),
///   or null when the property is absent.</param>
/// <param name="Destination">Broker coordinates the message died on, e.g. <c>topic/subscription</c>.</param>
/// <param name="EnvelopeJson">The message body, verbatim. Stored as-is.</param>
/// <param name="BrokerReason">The broker's dead-letter reason (e.g. MaxDeliveryAttemptsExceeded).</param>
/// <param name="BrokerDescription">The broker's dead-letter error description, when present.</param>
/// <param name="EnqueuedAt">When the broker first enqueued the message, when known.</param>
/// <param name="DeliveryCount">Broker delivery attempts before dead-lettering, when known.</param>
public sealed record BrokerDeadLetterImport(
  Guid MessageId,
  Guid? StreamId,
  string? MessageType,
  string Destination,
  string EnvelopeJson,
  string? BrokerReason,
  string? BrokerDescription,
  DateTimeOffset? EnqueuedAt,
  int? DeliveryCount);
