using System.Collections.Generic;
using System.Text.Json;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Strongly-typed message data for outbox publishing.
/// Replaces JsonDocument MessageData in OutboxRecord.
/// </summary>
public sealed class OutboxMessageData {
  /// <summary>Message identifier for correlation.</summary>
  public required MessageId MessageId { get; init; }

  /// <summary>Message payload as JsonElement (type-erased).</summary>
  public required JsonElement Payload { get; init; }

  /// <summary>Message hops for observability.</summary>
  public required List<MessageHop> Hops { get; init; }
}
