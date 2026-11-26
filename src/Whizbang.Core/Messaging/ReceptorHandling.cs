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
/// <para>
/// The handling is kept as a <see cref="Handling"/>, which also names the service and the receptor, so a strong id
/// derives from it (#2604). Disposing the scope ends that handling.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/message-cascade#every-dispatch-path-derives</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/ReceptorHandlingTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/Dispatcher/DispatcherDerivedIdentityInHandlingTests.cs</tests>
internal static class ReceptorHandling {
  private static readonly AsyncLocal<Handling?> _current = new();

  /// <summary>The envelope of the handling the current flow runs in, or null outside a receptor body.</summary>
  internal static IMessageEnvelope? Current => _current.Value?.Envelope;

  /// <summary>The handling the current flow runs in, or null outside a receptor body.</summary>
  internal static Handling? CurrentHandling => _current.Value;

  /// <summary>Enters <paramref name="envelope"/>'s handling, naming no service or receptor, until the scope is disposed.</summary>
  /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is null.</exception>
  internal static Scope Enter(IMessageEnvelope envelope) => Enter(envelope, serviceName: null, receptorId: null);

  /// <summary>
  /// Enters <paramref name="envelope"/>'s handling by <paramref name="serviceName"/>'s receptor <paramref name="receptorId"/>
  /// until the returned scope is disposed.
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is null.</exception>
  internal static Scope Enter(IMessageEnvelope envelope, string? serviceName, string? receptorId) {
    ArgumentNullException.ThrowIfNull(envelope);
    var previous = _current.Value;
    var handling = new Handling(envelope, serviceName ?? string.Empty, receptorId);
    _current.Value = handling;
    return new Scope(previous, handling);
  }

  /// <summary>
  /// The message a handling's derived ids anchor on. A nested local cascade hands receptors a wrapper that reports the
  /// inbound message's id and handler; their derivations anchor on the cascaded message that caused them (carried by the
  /// wrapper) so they cannot collide with the top-level handler's. Empty when the handling's envelope carries no id.
  /// </summary>
  internal static Guid AnchorOf(IMessageEnvelope handling) =>
    handling is CascadeEnvelopeWrapper { EmissionAnchor: { } nestedAnchor } ? nestedAnchor : handling.MessageId.Value;

  /// <summary>Ends the handling <see cref="Enter(IMessageEnvelope, string?, string?)"/> entered and restores the one before it.</summary>
  internal readonly struct Scope : IDisposable {
    private readonly Handling? _previous;
    private readonly Handling _entered;

    internal Scope(Handling? previous, Handling entered) {
      _previous = previous;
      _entered = entered;
    }

    /// <inheritdoc />
    public void Dispose() {
      _entered.End();
      _current.Value = _previous;
    }
  }
}
