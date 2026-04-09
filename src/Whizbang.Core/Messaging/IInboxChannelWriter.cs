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

  /// <summary>Signals that no more work will be written.</summary>
  void Complete();

  /// <summary>Event raised when new inbox work is available.</summary>
  event Action? OnNewInboxWorkAvailable;

  /// <summary>Fires OnNewInboxWorkAvailable to wake the publisher worker.</summary>
  void SignalNewInboxWorkAvailable();
}
