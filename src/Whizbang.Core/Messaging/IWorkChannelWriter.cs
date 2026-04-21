using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Interface for writing outbox work to a processing channel.
/// Enables the framework layer to queue work for background processing.
/// Implementations are typically singleton and shared between strategy and worker layers.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerChannelTests.cs:TestWorkChannelWriter</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TestWorkChannelWriter</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:TestWorkChannelWriter</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:TestWorkChannelWriter</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:TestWorkChannelWriter</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyImmediateProcessingTests.cs:TestWorkChannelWriter</tests>
public interface IWorkChannelWriter {
  /// <summary>
  /// Gets the channel reader for consumers (background workers).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerChannelTests.cs:TestWorkChannelWriter</tests>
  ChannelReader<OutboxWork> Reader { get; }
  /// <summary>
  /// Asynchronously writes outbox work to the channel for processing.
  /// Blocks if the channel is bounded and full.
  /// </summary>
  /// <param name="work">The outbox work to queue for processing</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>A task that completes when the work has been written to the channel</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerChannelTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyImmediateProcessingTests.cs:TestWorkChannelWriter</tests>
  ValueTask WriteAsync(OutboxWork work, CancellationToken ct = default);

  /// <summary>
  /// Attempts to write outbox work to the channel synchronously.
  /// Returns false if the channel is bounded and full, or if the channel is complete.
  /// </summary>
  /// <param name="work">The outbox work to queue for processing</param>
  /// <returns>True if the work was written; false if the channel is full or complete</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerChannelTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyImmediateProcessingTests.cs:TestWorkChannelWriter</tests>
  bool TryWrite(OutboxWork work);

  /// <summary>
  /// Signals that no more work will be written to the channel.
  /// Consumers will complete after draining existing work.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerChannelTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:TestWorkChannelWriter</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyImmediateProcessingTests.cs:TestWorkChannelWriter</tests>
  void Complete();

  /// <summary>
  /// Checks whether a message is currently in-flight (written to channel but not yet completed/failed).
  /// Used by the polling path to avoid re-queuing messages already being processed.
  /// </summary>
  /// <param name="messageId">The message ID to check</param>
  /// <returns>True if the message was written and not yet removed</returns>
  bool IsInFlight(Guid messageId);

  /// <summary>
  /// Removes a message from in-flight tracking after completion or failure.
  /// Called by the publisher when a message is successfully published or permanently failed.
  /// </summary>
  /// <param name="messageId">The message ID to remove from tracking</param>
  void RemoveInFlight(Guid messageId);

  /// <summary>
  /// Clears all in-flight tracking state.
  /// Used during test cleanup to prevent stale in-flight entries from blocking
  /// new messages in shared fixture scenarios.
  /// </summary>
  void ClearInFlight();

  /// <summary>
  /// Event raised when new outbox work has been stored and is ready for processing.
  /// The publisher worker subscribes to wake its coordinator loop immediately.
  /// </summary>
  /// <docs>operations/workers/publisher-worker#immediate-poll</docs>
  event Action? OnNewWorkAvailable;

  /// <summary>
  /// Fires <see cref="OnNewWorkAvailable"/> to wake the publisher worker immediately.
  /// Called by strategies after flushing new outbox messages to the database.
  /// </summary>
  void SignalNewWorkAvailable();

  /// <summary>
  /// Event raised when new perspective events have been stored and are ready for processing.
  /// The perspective worker subscribes to wake its processing loop immediately.
  /// </summary>
  /// <docs>operations/workers/perspective-worker#immediate-poll</docs>
  event Action? OnNewPerspectiveWorkAvailable;

  /// <summary>
  /// Fires <see cref="OnNewPerspectiveWorkAvailable"/> to wake the perspective worker immediately.
  /// Called by strategies after flushing new perspective events to the database.
  /// </summary>
  void SignalNewPerspectiveWorkAvailable();

  /// <summary>
  /// Checks whether an in-flight message's lease should be renewed.
  /// Returns true when the message has been in-flight for more than half the lease duration,
  /// preventing unnecessary lease renewals on every tick while ensuring the lease doesn't expire.
  /// </summary>
  /// <param name="messageId">The message ID to check</param>
  /// <returns>True if the message needs a lease renewal</returns>
  bool ShouldRenewLease(Guid messageId);
}
