// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Derives the stream id a <c>[GenerateStreamId]</c> message starts from the id of the message itself (#2274), so a
/// retry of the handling that produced the message, which re-derives the message's id, starts the same stream
/// instead of a second one that nothing else refers to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Layout:</strong> the shared derived layout (see <see cref="DerivedIdentity"/>) at the message's own
/// position: the message id's first 92 bits (its millisecond, counter and ordinal), then the low 36 bits of SHA-256
/// over the UTF-8 canonical string <c>whizbang.generated-stream.v1\n{messageId:N}</c>. The stream therefore sorts
/// with the message that started it, stays a version 7 id, and differs from the message's own id.
/// </para>
/// <para>
/// A minted message id (a publish outside any handling) gives a fresh stream as before; a derived one gives the
/// stream its first run started. The value is a compatibility contract: a stored stream id is referenced by every
/// record on it, so changing the layout changes the stream a retry across the deploy starts.
/// </para>
/// <para>
/// A command a receptor body sends (#2537), and an event it invokes locally (#2552), generates its stream before its own
/// id exists (the stream is set on the message before any envelope is built, and the void local invoke builds none), so
/// <see cref="DeriveInHandling"/> derives it from the handling instead. A command's own id derives from the handling too
/// since #2543 (<see cref="SentMessageIdentity"/>).
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/GeneratedStreamIdentityTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/Dispatcher/DispatcherDerivedIdentityInHandlingTests.cs</tests>
public static class GeneratedStreamIdentity {
  private const string PREFIX = "whizbang.generated-stream.v1";
  private const string IN_HANDLING_PREFIX = "whizbang.generated-stream.in-handling.v1";

  /// <summary>Derives the stream id the message <paramref name="messageId"/> starts.</summary>
  /// <param name="messageId">The id of the message that starts the stream. Must not be empty.</param>
  /// <returns>A version 7 id that is identical for an identical message id.</returns>
  /// <exception cref="ArgumentException">The message id is empty.</exception>
  public static Guid Derive(Guid messageId) {
    if (messageId == Guid.Empty) {
      throw new ArgumentException("A generated stream id needs the id of the message that starts the stream.", nameof(messageId));
    }
    return DerivedIdentity.AtTheSamePosition(messageId, $"{PREFIX}\n{messageId:N}");
  }

  /// <summary>
  /// Derives the stream id a message starts when it is dispatched inside a handling before its own id exists (a command
  /// sent from a receptor body, #2537; an event invoked locally, #2552): from the handled message <paramref name="causeId"/>,
  /// the service, the handler, the message type and <paramref name="ordinal"/>, the message's position among the streams
  /// the handling starts this way (events and other messages count separately). A redelivered handling sends again in the same order and starts the same streams.
  /// </summary>
  /// <remarks>
  /// <strong>Layout:</strong> the shared derived layout (see <see cref="DerivedIdentity"/>) at the cause's position:
  /// the cause's first 80 bits, the ordinal, then the low 36 bits of SHA-256 over the UTF-8 canonical string
  /// <c>whizbang.generated-stream.in-handling.v1\n{cause:N}\n{service}\n{handler}\n{type}\n{ordinal}</c>. The value is
  /// a compatibility contract, as <see cref="Derive"/>'s is.
  /// </remarks>
  /// <param name="causeId">The id of the message being handled. Must not be empty.</param>
  /// <param name="serviceName">The sending service's name.</param>
  /// <param name="handlerName">The handler (inbox row) within the service, or null when the handling has none.</param>
  /// <param name="messageTypeName">The sent message's type, rendered by <see cref="TypeNameFormatter"/>.</param>
  /// <param name="ordinal">Zero-based position of the stream among those the handling starts this way.</param>
  /// <returns>A version 7 id that is identical for identical inputs.</returns>
  /// <exception cref="ArgumentException">The cause id is empty.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The ordinal is negative.</exception>
  public static Guid DeriveInHandling(Guid causeId, string serviceName, string? handlerName, string messageTypeName, int ordinal) {
    if (causeId == Guid.Empty) {
      throw new ArgumentException("A stream derived from a handling needs the id of the message being handled.", nameof(causeId));
    }
    ArgumentNullException.ThrowIfNull(serviceName);
    ArgumentNullException.ThrowIfNull(messageTypeName);
    ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

    var canonical = string.Concat(
      IN_HANDLING_PREFIX, "\n",
      causeId.ToString("N"), "\n",
      serviceName, "\n",
      handlerName ?? string.Empty, "\n",
      messageTypeName, "\n",
      ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return DerivedIdentity.FromCanonical(causeId, ordinal, canonical);
  }
}
