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
}
