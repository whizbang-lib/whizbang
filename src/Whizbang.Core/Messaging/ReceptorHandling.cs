// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The handling a receptor body runs in: the envelope of the message the receptor was invoked for (#2274).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReceptorInvoker"/> enters it around every receptor body. Inside it, every message the body dispatches,
/// on any dispatch path (#2543), derives its id from the handling: an event exactly as an event the body returns does
/// (see <see cref="EmissionIdentity"/>), on the same ordinals, because both pass this one envelope instance to the
/// dispatcher; any other message on ordinals of its own (see <see cref="SentMessageIdentity"/>). A retry of the handling
/// materializes a fresh envelope, starts again at ordinal zero and re-derives every id, so the stored copies of the
/// first run absorb the second run's instead of a republish.
/// </para>
/// <para>
/// Outside a receptor body there is no handling and a dispatch mints a fresh id, as before. The handling flows with
/// the execution context, so work the body starts and awaits (a local receptor of what it publishes) is part of it.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/ReceptorHandlingTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/Dispatcher/DispatcherDerivedIdentityInHandlingTests.cs</tests>
internal static class ReceptorHandling {
  private static readonly AsyncLocal<IMessageEnvelope?> _current = new();

  /// <summary>The envelope of the handling the current flow runs in, or null outside a receptor body.</summary>
  internal static IMessageEnvelope? Current => _current.Value;

  /// <summary>Enters <paramref name="envelope"/>'s handling until the returned scope is disposed.</summary>
  /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is null.</exception>
  internal static Scope Enter(IMessageEnvelope envelope) {
    ArgumentNullException.ThrowIfNull(envelope);
    var previous = _current.Value;
    _current.Value = envelope;
    return new Scope(previous);
  }

  /// <summary>Restores the handling that was current before <see cref="Enter"/>.</summary>
  internal readonly struct Scope : IDisposable {
    private readonly IMessageEnvelope? _previous;

    internal Scope(IMessageEnvelope? previous) => _previous = previous;

    /// <inheritdoc />
    public void Dispose() => _current.Value = _previous;
  }
}
