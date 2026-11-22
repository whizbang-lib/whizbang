// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Derives the stream id a <c>[GenerateStreamId]</c> message starts from the id of the message itself.
/// </summary>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/GeneratedStreamIdentityTests.cs</tests>
public static class GeneratedStreamIdentity {
  /// <summary>Derives the stream id the message <paramref name="messageId"/> starts.</summary>
  public static Guid Derive(Guid messageId) => messageId == Guid.Empty ? Guid.Empty : ValueObjects.TrackedGuid.New();
}
