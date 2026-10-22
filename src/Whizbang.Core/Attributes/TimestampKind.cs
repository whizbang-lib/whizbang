// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Attributes;

/// <summary>
/// Specifies the kind of timestamp to auto-populate on a message property.
/// Each kind corresponds to a specific point in the message lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Use with <see cref="PopulateTimestampAttribute"/> to automatically capture
/// timestamps at different stages of message processing.
/// </para>
/// <para>
/// <list type="bullet">
/// <item><description><see cref="SentAt"/> - When dispatcher.SendAsync/PublishAsync is called</description></item>
/// <item><description><see cref="QueuedAt"/> - When the dispatcher writes the message to the outbox</description></item>
/// <item><description><see cref="DeliveredAt"/> - When message arrives from transport</description></item>
/// </list>
/// </para>
/// </remarks>
/// <docs>extending/attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/PopulateTimestampAttributeTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/PopulateTimestampAttributeTests.cs:TimestampKind_AllValues_AreDistinctAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/PopulateTimestampAttributeTests.cs:TimestampKind_HasThreeValuesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/PopulateTimestampAttributeTests.cs:PopulateTimestampAttribute_MultipleTimestamps_OnSameRecord_AreAllDiscoverableAsync</tests>
public enum TimestampKind {
  /// <summary>
  /// Populated when dispatcher.SendAsync() or PublishAsync() is called.
  /// Captures the moment the message enters the dispatch pipeline.
  /// </summary>
  SentAt = 0,

  /// <summary>
  /// Populated when the dispatcher writes the message to the outbox, from the registered
  /// <see cref="TimeProvider"/>. The stored copy, and so the one the transport delivers, carries it.
  /// Only fires for messages that get an outbox copy (not local-only dispatch).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherQueuedAtStampTests.cs:Dispatch_WritesOutboxCopy_StampsQueuedAtFromTheTimeProviderAsync</tests>
  QueuedAt = 1,

  /// <summary>
  /// Populated when the message arrives at the destination inbox.
  /// Captures the moment the message is received from transport.
  /// Only fires for distributed messages (not local-only dispatch).
  /// </summary>
  DeliveredAt = 2
}
