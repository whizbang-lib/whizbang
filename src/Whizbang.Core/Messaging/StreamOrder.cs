// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The order a worker hands a stream on in: the position each work item carries, never its id (#2551).
/// </summary>
/// <remarks>
/// <para>
/// An event or message id is time-ordered and never a stream position: an id derived inside a handling sorts at its
/// cause, so an effect can sort ahead of the event that caused it. The reads rank a stream and the rank travels on the
/// item: a fetched inbox row carries <see cref="InboxBatchRow.StreamPosition"/> (a received event's stored version, a
/// command's arrival), and a read event carries its <see cref="MessageEnvelope{T}.LocalStreamVersion"/>. Every worker
/// and runner orders a stream through this class, and a source guard (<c>StreamOrderSourceGuardTests</c>) fails on a
/// sort by id anywhere in them.
/// </para>
/// <para>
/// Both orderings are stable: items without a position keep the order the read returned them in, which the reads
/// already rank by stream position. An item without a position goes after the positioned ones.
/// </para>
/// </remarks>
/// <docs>fundamentals/event-store/version-ordering#dispatch-order</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/StreamOrderTests.cs</tests>
public static class StreamOrder {
  /// <summary>Orders fetched inbox rows by the stream position the fetch ranked them at.</summary>
  /// <param name="rows">The rows of one stream, or of several streams grouped afterwards.</param>
  /// <returns>The rows by <see cref="InboxBatchRow.StreamPosition"/>, the fetch's own order breaking a tie.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/StreamOrderTests.cs:InboxRows_AreOrderedByTheirStreamPosition_NotByIdAsync</tests>
  public static List<InboxBatchRow> InStreamOrder(IEnumerable<InboxBatchRow> rows) {
    ArgumentNullException.ThrowIfNull(rows);
    return [.. rows.OrderBy(static r => r.StreamPosition <= 0 ? long.MaxValue : r.StreamPosition)];
  }

  /// <summary>Orders fetched outbox rows by their emission position, the order their stream was emitted in (#2551).</summary>
  /// <param name="rows">The rows of one stream, or of several streams grouped afterwards.</param>
  /// <returns>The rows by <see cref="OutboxBatchRow.StreamPosition"/>, the fetch's own order breaking a tie.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/StreamOrderTests.cs:OutboxRows_AreOrderedByTheirEmissionPosition_NotByIdAsync</tests>
  public static List<OutboxBatchRow> InStreamOrder(IEnumerable<OutboxBatchRow> rows) {
    ArgumentNullException.ThrowIfNull(rows);
    return [.. rows.OrderBy(static r => r.StreamPosition <= 0 ? long.MaxValue : r.StreamPosition)];
  }

  /// <summary>Orders one stream's events by the version each carries.</summary>
  /// <param name="events">The events of one stream.</param>
  /// <returns>The events by <see cref="MessageEnvelope{T}.LocalStreamVersion"/>, the read's own order breaking a tie.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/StreamOrderTests.cs:Events_AreOrderedByTheirVersion_NotByIdAsync</tests>
  public static List<MessageEnvelope<IEvent>> InStreamOrder(IEnumerable<MessageEnvelope<IEvent>> events) {
    ArgumentNullException.ThrowIfNull(events);
    return [.. events.OrderBy(static e => e.LocalStreamVersion ?? long.MaxValue)];
  }
}
