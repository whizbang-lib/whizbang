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
/// </remarks>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/GeneratedStreamIdentityTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/Dispatcher/DispatcherDerivedIdentityInHandlingTests.cs</tests>
public static class GeneratedStreamIdentity {
  private const string PREFIX = "whizbang.generated-stream.v1";

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
}
