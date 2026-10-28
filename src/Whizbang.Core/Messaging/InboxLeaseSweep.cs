// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>Where an inbox row this process holds is in its life here.</summary>
/// <docs>fundamentals/work-coordinator/inbox-dispatch#held-rows</docs>
public enum InboxHoldState {
  /// <summary>Written to the inbox channel, waiting for a dispatch consumer.</summary>
  Queued = 0,

  /// <summary>A dispatch consumer is running it.</summary>
  Dispatching = 1,

  /// <summary>Dispatched; its completion waits in the batched commit channel.</summary>
  CommitPending = 2,
}

/// <summary>
/// One pass over the inbox rows a process holds (#1788): the rows whose leases are due for renewal, and the rows that
/// have used every renewal they are allowed and are no longer held.
/// </summary>
/// <param name="Renew">Rows to renew now. Their renewal is already counted.</param>
/// <param name="Lapsed">Rows past their renewal cap, removed from the held set by the sweep.</param>
/// <docs>fundamentals/work-coordinator/inbox-dispatch#held-rows</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/InboxChannelWriterHeldRowsTests.cs</tests>
public sealed record InboxLeaseSweep(IReadOnlyList<Guid> Renew, IReadOnlyList<LapsedInboxLease> Lapsed) {
  /// <summary>A sweep that found nothing to renew or release.</summary>
  public static InboxLeaseSweep Empty { get; } = new([], []);
}

/// <summary>A held inbox row whose renewals ran out (#1788).</summary>
/// <param name="MessageId">The row.</param>
/// <param name="MessageType">The row's message type, for the lapse log.</param>
/// <param name="State">Where the row was when its renewals ran out.</param>
/// <param name="Renewals">Renewals spent in that state.</param>
/// <docs>fundamentals/work-coordinator/inbox-dispatch#held-rows</docs>
public sealed record LapsedInboxLease(Guid MessageId, string MessageType, InboxHoldState State, int Renewals);
