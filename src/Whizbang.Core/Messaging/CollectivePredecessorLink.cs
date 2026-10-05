// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Which outgoing messages get a predecessor link (#1003), and under what type they are named as a predecessor.
/// </summary>
/// <remarks>
/// <para>
/// The link itself is made by the store, not the publisher: <c>store_outbox_messages</c> (migration 190) keeps, per
/// ordering key, the last collective stored on it, and in the transaction that stores the next one writes the
/// previous one's id and type into its payload and moves the key's head to it. The head row's lock holds to commit,
/// so every instance of every publisher of a key forms one chain, and a restart does not break it.
/// </para>
/// <para>
/// The publisher only marks the message: <see cref="OutboxMessage.CollectiveLinkType"/> carries
/// <see cref="TypeFor"/>. A store or a publisher that does not know the mark stores the collective as before, and it
/// applies as a collective always did.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events#ordering-across-services</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CollectivePredecessorLinkTests.cs</tests>
public static class CollectivePredecessorLink {
  /// <summary>
  /// The type a linked message is named by when it is a predecessor, in the form the event store records event
  /// types, or null when the message gets no link: anything but a <see cref="CollectiveEventBase"/> with an ordering
  /// key. A hand-written <see cref="ICollectiveEvent"/> has nowhere to carry a link, so it gets none.
  /// </summary>
  /// <param name="payload">The message being published.</param>
  /// <returns>The predecessor type name, or null.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CollectivePredecessorLinkTests.cs:TypeFor_AKeyedCollective_IsItsEventStoreTypeNameAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CollectivePredecessorLinkTests.cs:TypeFor_AnythingButAKeyedCollectiveBase_IsNullAsync</tests>
  public static string? TypeFor(object? payload) =>
    payload is CollectiveEventBase { OrderingKey: { } key } && !string.IsNullOrWhiteSpace(key)
      ? TypeNameFormatter.Format(payload.GetType())
      : null;
}
