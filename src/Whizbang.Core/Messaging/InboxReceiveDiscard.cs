// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Why a consumer skipped a received message: the value of <see cref="InboxMessage.DiscardedOnReceive"/>, the reason the
/// stored row records, and the <c>reason</c> tag of <c>whizbang.transport.inbox.messages_discarded</c> (#1720).
/// </summary>
/// <docs>messaging/transports/transport-consumer#batch-handler</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWorkerStoresBeforeSkippingTests.cs</tests>
public static class InboxReceiveDiscard {
#pragma warning disable CA1707 // Identifiers should not contain underscores (constant naming convention)
  /// <summary>No receptor, perspective or runtime receptor on this service consumes the message's type.</summary>
  public const string NO_LOCAL_CONSUMER = "no_local_consumer";

  /// <summary>The envelope is directed at another service (its target names a different service).</summary>
  public const string FOREIGN_TARGET = "foreign_target";

  /// <summary>An event of a namespace this service owns, delivered back to it: its own event, already stored here.</summary>
  public const string OWNED_ECHO = "owned_echo";

  /// <summary>A message of a namespace this service owns whose last hop is this service: its own send, delivered back.</summary>
  public const string SELF_ECHO = "self_echo";

  /// <summary>An event whose type is not among the event types this service knew when it started receiving.</summary>
  public const string UNKNOWN_EVENT_TYPE = "unknown_event_type";
#pragma warning restore CA1707
}
