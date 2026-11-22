// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The handling a receptor body runs in: the envelope of the message the receptor was invoked for.
/// </summary>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/ReceptorHandlingTests.cs</tests>
internal static class ReceptorHandling {
  /// <summary>The envelope of the handling the current flow runs in, or null outside a receptor body.</summary>
  internal static IMessageEnvelope? Current => null;

  /// <summary>Enters <paramref name="envelope"/>'s handling until the returned scope is disposed.</summary>
  internal static Scope Enter(IMessageEnvelope envelope) {
    ArgumentNullException.ThrowIfNull(envelope);
    return default;
  }

  /// <summary>Restores the handling that was current before <see cref="Enter"/>.</summary>
  internal readonly struct Scope : IDisposable {
    /// <inheritdoc />
    public void Dispose() {
      // RED stub
    }
  }
}
