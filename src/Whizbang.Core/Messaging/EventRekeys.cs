// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The events an upcaster re-keyed on read: those whose <c>[StreamId]</c> it moved to another stream (#1798).
/// </summary>
/// <remarks>
/// <para>
/// Every path keys a perspective row by the stream an event is stored on: live apply, the rewind and a rebuild. The one
/// exception is a re-key upcaster (<see cref="IEventUpcaster"/>), which moves an event to another stream as it is read;
/// a rebuild routes such an event onto the row it now names. A stored event whose payload merely names another stream,
/// with no upcaster involved, is not a re-key and stays on its stream: routing it by its payload is what let a rebuild
/// of one stream replace another stream's row with its own fold.
/// </para>
/// <para>
/// <see cref="UpcastingEventStoreDecorator"/> records a re-key when it reads one, keyed by the payload instance (weakly,
/// so nothing is kept alive), and a rebuild asks here before it routes. An upcaster that changes the payload in place
/// is detected too: the decorator reads the stream before the pipeline runs.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/UpcastingEventStoreDecoratorTests.cs:ReadPolymorphicAsync_UpcasterMovesTheStreamId_RecordsTheRekeyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/UpcastingEventStoreDecoratorTests.cs:RekeyedTo_ForAnEventNeverRead_IsNullAsync</tests>
public static class EventRekeys {
  private static readonly ConditionalWeakTable<IEvent, StrongBox<Guid>> _rekeys = [];

  /// <summary>The stream an upcaster moved <paramref name="event"/> to as it was read, or null when none did.</summary>
  /// <param name="event">An event as a read returned it.</param>
  /// <returns>The stream it was re-keyed to, or null.</returns>
  public static Guid? RekeyedTo(IEvent @event) =>
    _rekeys.TryGetValue(@event, out var to) ? to.Value : null;

  /// <summary>Records that an upcaster moved <paramref name="event"/> to <paramref name="to"/>.</summary>
  internal static void Record(IEvent @event, Guid to) => _rekeys.AddOrUpdate(@event, new StrongBox<Guid>(to));
}
