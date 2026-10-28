// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Interface for writing inbox work to a processing channel.
/// Mirrors IWorkChannelWriter pattern — callers check IsInFlight before writing,
/// RemoveInFlight after completion is acknowledged by DB.
/// </summary>
/// <docs>messaging/inbox-pattern</docs>
/// <tests>tests/Whizbang.Core.Integration.Tests/WorkCoordinatorStrategyChannelIntegrationTests.cs</tests>
public interface IInboxChannelWriter {
  /// <summary>Gets the channel reader for consumers (InboxDispatchWorker).</summary>
  ChannelReader<InboxWork> Reader { get; }

  /// <summary>Asynchronously writes inbox work to the channel.</summary>
  ValueTask WriteAsync(InboxWork work, CancellationToken ct = default);

  /// <summary>Attempts to write inbox work to the channel synchronously.</summary>
  bool TryWrite(InboxWork work);

  /// <summary>Returns true if the message is currently in-flight (queued or being processed).</summary>
  bool IsInFlight(Guid messageId);

  /// <summary>Removes a message from in-flight tracking after completion is acknowledged by DB.</summary>
  void RemoveInFlight(Guid messageId);

  /// <summary>Returns true if the message has been in-flight long enough to need a lease renewal.</summary>
  bool ShouldRenewLease(Guid messageId);

  /// <summary>
  /// Takes the row's dispatch ticket (#1788): true for the first copy of a held, queued row to reach a dispatch
  /// consumer; false for a row no longer held (its renewals ran out and it was released) or a duplicate copy of one
  /// already dispatching. A writer that does not track rows lets every copy through.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/inbox-dispatch#held-rows</docs>
  bool TryBeginDispatch(Guid messageId) => true;

  /// <summary>Records that the row's completion was handed to the batched commit channel, so it stays held until the commit lands (#1788).</summary>
  /// <docs>fundamentals/work-coordinator/inbox-dispatch#held-rows</docs>
  void MarkCommitPending(Guid messageId) { }

  /// <summary>Ends the row's dispatch: it stops being held unless its commit is pending (#1788).</summary>
  /// <docs>fundamentals/work-coordinator/inbox-dispatch#held-rows</docs>
  void EndDispatch(Guid messageId) { }

  /// <summary>
  /// Counts the held rows whose leases are due for renewal and removes those past their renewal cap (#1788).
  /// </summary>
  /// <param name="renewAfter">How long after its last renewal (or its arrival) a row's lease is renewed.</param>
  /// <param name="maxRenewals">Renewals a row may take in one state before it is no longer held.</param>
  /// <docs>fundamentals/work-coordinator/inbox-dispatch#held-rows</docs>
  InboxLeaseSweep SweepLeases(TimeSpan renewAfter, int maxRenewals) => InboxLeaseSweep.Empty;

  /// <summary>
  /// Payload bytes of the rows this process holds (#1793): queued, dispatching or waiting for their commit, measured as
  /// each payload's UTF-8 JSON. The drain admits no row that would take it past its working-set budget. Zero for a
  /// writer that does not track rows.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/inbox-dispatch#held-rows</docs>
  long HeldPayloadBytes => 0;


  /// <summary>Signals that no more work will be written.</summary>
  void Complete();

  /// <summary>Event raised when new inbox work is available.</summary>
  event Action? OnNewInboxWorkAvailable;

  /// <summary>Fires OnNewInboxWorkAvailable to wake the publisher worker.</summary>
  void SignalNewInboxWorkAvailable();
}
