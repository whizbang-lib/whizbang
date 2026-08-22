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
/// <docs>messaging/inbox-channel</docs>
/// <tests>tests/Whizbang.Core.Integration.Tests/WorkCoordinatorStrategyChannelIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/InboxChannelWriterOutstandingTests.cs</tests>
public interface IInboxChannelWriter {
  /// <summary>Gets the channel reader for consumers (WorkCoordinatorPublisherWorker).</summary>
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
  /// How many messages are currently claimed-and-handed-off but not yet completed.
  /// </summary>
  /// <remarks>
  /// This is the quantity a claim loop must gate on. Bounding the size of each claim does not bound
  /// this: a loop that claims and immediately claims again accumulates outstanding work across
  /// cycles regardless of how small each batch is, until the whole backlog is held and its leases
  /// lapse together.
  /// </remarks>
  int InFlightCount { get; }

  /// <summary>
  /// Drops in-flight entries older than <paramref name="age"/>, returning how many were removed.
  /// </summary>
  /// <remarks>
  /// The escape hatch from a permanent stall. If work is held but nothing completes, a loop that
  /// gates on <see cref="InFlightCount"/> would never claim again. Entries older than the lease no
  /// longer represent work this instance owns — the lease has lapsed and the store will re-issue
  /// the rows — so ageing them out makes the local count match reality and reopens the gate.
  /// </remarks>
  int PruneInFlightOlderThan(TimeSpan age);

  /// <summary>Signals that no more work will be written.</summary>
  void Complete();

  /// <summary>Event raised when new inbox work is available.</summary>
  event Action? OnNewInboxWorkAvailable;

  /// <summary>Fires OnNewInboxWorkAvailable to wake the publisher worker.</summary>
  void SignalNewInboxWorkAvailable();
}
