// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The streams a handling has started for messages sent under minted ids (#2537), one per message instance, each at
/// the handling's next ordinal. Keyed by the handling's envelope instance, as <see cref="EmissionSequence"/> is, so a
/// redelivery, which materializes a fresh envelope, starts again at zero and derives the same streams in the same
/// order. The ordinals are the handling's own for this purpose: sending a command does not shift the ordinals, and so
/// the ids, of the events the handling returns or publishes.
/// </summary>
/// <remarks>
/// A send generates a <c>[GenerateStreamId]</c> stream more than once on some paths: before the receptor sees the
/// message, again when its envelope is built, and again for the envelope a stored copy's local run is registered under
/// (#1464). Without the memo each call would take an ordinal and, without <c>OnlyIfEmpty</c>, replace the stream, so the
/// stored copy and the local run would name different streams. The memo makes every call for one message in one
/// handling return the stream the first call derived.
/// <para>
/// Outside a handling a stream is minted rather than derived, still once per message instance (<see cref="GetOrMint"/>,
/// #2539), so every envelope one send builds names one stream.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/GeneratedStreamAssignmentsTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/Dispatcher/DispatcherDerivedIdentityInHandlingTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatchTangentialParityComponentTests.cs</tests>
internal static class GeneratedStreamAssignments {
  private static readonly ConditionalWeakTable<IMessageEnvelope, Handling> _handlings = [];
  private static readonly ConditionalWeakTable<object, StrongBox<Guid>> _minted = [];

  /// <summary>
  /// Returns the stream minted for <paramref name="message"/> outside a handling: the one an earlier call for the same
  /// instance minted, otherwise a fresh time-ordered one (#2539). One send builds several envelopes for one message (the
  /// local run's, the stored copy's, the one the copy's local run is registered under), and each used to mint again,
  /// so the stored copy and the local run named different streams.
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
  internal static Guid GetOrMint(object message) {
    ArgumentNullException.ThrowIfNull(message);
    return _minted.GetValue(message, static _ => new StrongBox<Guid>(ValueObjects.TrackedGuid.New())).Value;
  }

  /// <summary>
  /// Returns the stream <paramref name="message"/> starts in <paramref name="handling"/>: the one an earlier call for
  /// the same instance derived, otherwise <paramref name="derive"/> of the handling's next ordinal.
  /// </summary>
  /// <exception cref="ArgumentNullException">An argument is null.</exception>
  internal static Guid GetOrAdd(IMessageEnvelope handling, object message, Func<int, Guid> derive) {
    ArgumentNullException.ThrowIfNull(handling);
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(derive);
    var state = _handlings.GetValue(handling, static _ => new Handling());
    return state.Streams.GetValue(message, _ => new StrongBox<Guid>(derive(Interlocked.Increment(ref state.Next) - 1))).Value;
  }

  /// <summary>Not yet implemented (#2552): records nothing.</summary>
  internal static void Assign(IMessageEnvelope? handling, object message, Guid stream) {
    _ = (handling, message, stream);
  }

  private sealed class Handling {
    public readonly ConditionalWeakTable<object, StrongBox<Guid>> Streams = [];
    public int Next;
  }
}
