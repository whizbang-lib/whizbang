// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Derives the id of a message other than an event (a command sent or invoked locally, a query) that a dispatch
/// creates inside a receptor's handling (#2543), so a redelivered handling sends and invokes under the ids of its first
/// run instead of minting new ones.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Layout:</strong> the shared derived layout (see <see cref="DerivedIdentity"/>) at the cause's position: the
/// handled message's first 80 bits, the message's ordinal among the messages of this kind the handling has created, then
/// the low 36 bits of SHA-256 over the UTF-8 canonical string
/// <c>whizbang.sent-message.in-handling.v1\n{cause:N}\n{service}\n{handler}\n{type}\n{ordinal}</c>. It sorts with the
/// handling, the handling's sends sort in the order it made them, and it stays a version 7 id.
/// </para>
/// <para>
/// An event a handling creates takes <see cref="EmissionIdentity"/> on the handling's emission ordinals instead. These
/// ordinals are the handling's own (<see cref="NextOrdinal"/>): sending a command does not shift the ordinals, and so the
/// ids, of the events the handling returns or publishes, as a command's generated stream does not
/// (<see cref="GeneratedStreamIdentity.DeriveInHandling"/>). The canonical prefix keeps the two apart where they share a
/// cause and an ordinal.
/// </para>
/// <para>
/// The value is a compatibility contract: a stored message id is what the receiving side deduplicates on, so changing the
/// layout changes the id a redelivery across the deploy sends under.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/SentMessageIdentityTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/Dispatcher/DispatcherSendPathsDerivedIdentityTests.cs</tests>
#pragma warning disable RCS1213, S1144, RCS1163, IDE0060 // RED stub (#2543)
public static class SentMessageIdentity {
  private const string PREFIX = "whizbang.sent-message.in-handling.v1";

  private static readonly ConditionalWeakTable<IMessageEnvelope, StrongBox<int>> _ordinals = [];

  /// <summary>
  /// Derives the id of the <paramref name="ordinal"/>th message of <paramref name="messageTypeName"/> created while
  /// <paramref name="serviceName"/> handles <paramref name="causeId"/> in <paramref name="handlerName"/>.
  /// </summary>
  /// <param name="causeId">The id of the message being handled. Must not be empty.</param>
  /// <param name="serviceName">The sending service's name.</param>
  /// <param name="handlerName">The handler (inbox row) within the service, or null when the handling has none.</param>
  /// <param name="messageTypeName">The created message's type, rendered by <see cref="TypeNameFormatter"/>.</param>
  /// <param name="ordinal">Zero-based position of the message among those the handling creates this way.</param>
  /// <returns>A version 7 id that is identical for identical inputs.</returns>
  /// <exception cref="ArgumentException">The cause id is empty.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The ordinal is negative.</exception>
  public static Guid Derive(Guid causeId, string serviceName, string? handlerName, string messageTypeName, int ordinal) {
    return Guid.Empty; // RED stub (#2543)
  }

  /// <summary>
  /// Returns the next zero-based ordinal for a message other than an event created in <paramref name="handling"/>. Keyed
  /// by the handling's envelope instance, as <see cref="EmissionSequence"/> is, so a redelivery, which materializes a
  /// fresh envelope, starts again at zero.
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="handling"/> is null.</exception>
  internal static int NextOrdinal(IMessageEnvelope handling) {
    return 0; // RED stub (#2543)
  }
}
#pragma warning restore RCS1213, S1144, RCS1163, IDE0060
