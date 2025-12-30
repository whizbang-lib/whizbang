using System.Collections.Generic;
using System.Text.Json;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Strongly-typed message data for inbox processing.
/// Replaces JsonDocument MessageData in InboxRecord.
/// </summary>
public sealed class InboxMessageData {
  /// <summary>Message identifier for correlation.</summary>
  public required MessageId MessageId { get; init; }

  /// <summary>Message payload as JsonElement (type-erased).</summary>
  public required JsonElement Payload { get; init; }

  /// <summary>Message hops for observability.</summary>
  public required List<MessageHop> Hops { get; init; }
}
