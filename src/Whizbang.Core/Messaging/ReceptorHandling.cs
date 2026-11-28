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

  /// <summary>
  /// The scheme this build runs a handling under (#2633), and stamps on an inbox row the first time it runs the row: a
  /// receptor invoked locally from inside a handling runs in a handling of its own.
  /// </summary>
  internal static HandlingDerivationScheme CurrentScheme => HandlingDerivationScheme.OwnHandling;

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
  /// Enters the handling the body of <paramref name="serviceName"/>'s receptor <paramref name="receptorId"/> runs in when
  /// generated local dispatch reaches it (#2633), until the returned scope is disposed. Only when a handling is already
  /// active: the receptor then runs in a handling of its own, anchored on the id
  /// <see cref="LocalInvocationIdentity"/> derives at the outer handling's next local-invocation ordinal and carried by a
  /// wrapper envelope, the rule a nested local cascade follows. At top level it enters nothing and costs one ambient read:
  /// a top-level local invoke has no handled message, so <c>DeriveFrom</c> keeps throwing there. Under
  /// <see cref="HandlingDerivationScheme.SharedWithCaller"/> (a row that first ran on an earlier build), in an ended
  /// handling, and in one whose message has no id, it enters nothing either, and the receptor shares the outer handling.
  /// </summary>
  internal static LocalReceptorHandlingScope EnterLocalInvocation(string serviceName, string receptorId) {
    var outer = _current.Value;
    return outer is null ? default : _enterNestedLocalInvocation(outer, serviceName, receptorId);
  }

  private static LocalReceptorHandlingScope _enterNestedLocalInvocation(Handling outer, string serviceName, string receptorId) {
    if (!outer.IsActive || outer.Scheme == HandlingDerivationScheme.SharedWithCaller) {
      return default;
    }
    var envelope = outer.Envelope!;
    var outerAnchor = AnchorOf(envelope);
    if (outerAnchor == Guid.Empty) {
      return default;
    }
    var anchor = LocalInvocationIdentity.Derive(outerAnchor, serviceName, envelope.DispatchContext.HandlerName, outer.ReceptorId,
      LocalInvocationIdentity.NextOrdinal(envelope));
    return new LocalReceptorHandlingScope(Enter(new CascadeEnvelopeWrapper(envelope) { EmissionAnchor = anchor }, serviceName, receptorId));
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
