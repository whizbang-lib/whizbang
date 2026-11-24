// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The streams a handling has started for messages that have no id of their own to derive one from when the stream is
/// generated (a command sent, #2537; an event invoked locally, #2552), one per message instance, each at the handling's
/// next ordinal. Keyed by the handling's envelope instance, as <see cref="EmissionSequence"/> is, so a
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
/// <para>
/// A stream derived from the message's own id (a published or cascaded event, #2274) is recorded with
/// <see cref="Assign"/>, so the envelope the publish's local run builds afterwards names that stream instead of
/// generating a second one (#2552).
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
  /// the same instance derived or assigned, otherwise <paramref name="derive"/> of the handling's next ordinal. An
  /// event (one invoked locally, #2552) and any other message (a command sent, #2537) take ordinals of their own, so
  /// invoking an event does not move the streams of the commands the handling sends.
  /// </summary>
  /// <exception cref="ArgumentNullException">An argument is null.</exception>
  internal static Guid GetOrAdd(IMessageEnvelope handling, object message, Func<int, Guid> derive) {
    ArgumentNullException.ThrowIfNull(handling);
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(derive);
    var state = _handlings.GetValue(handling, static _ => new Handling());
    return state.Streams.GetValue(message, _ => new StrongBox<Guid>(derive(message is IEvent
      ? Interlocked.Increment(ref state.NextEvent) - 1
      : Interlocked.Increment(ref state.Next) - 1))).Value;
  }

  /// <summary>
  /// Records <paramref name="stream"/>, derived from the id <paramref name="message"/> was just given (a publish or a
  /// cascade, #2274), as the stream the instance names in <paramref name="handling"/>, or outside any handling when it
  /// is null (#2552). It replaces whatever the instance named before, as a new id starts a new stream, and takes no
  /// ordinal. The envelopes the same dispatch builds afterwards (the one a local run registers for its receptors) then
  /// read this stream through <see cref="GetOrAdd"/> or <see cref="GetOrMint"/> instead of starting another.
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
  internal static void Assign(IMessageEnvelope? handling, object message, Guid stream) {
    ArgumentNullException.ThrowIfNull(message);
    var streams = handling is null ? _minted : _handlings.GetValue(handling, static _ => new Handling()).Streams;
    streams.AddOrUpdate(message, new StrongBox<Guid>(stream));
  }

  private sealed class Handling {
    public readonly ConditionalWeakTable<object, StrongBox<Guid>> Streams = [];
    public int Next;
    public int NextEvent;
  }
}
