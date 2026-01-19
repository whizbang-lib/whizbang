using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Coordinates work batch processing by calling IWorkCoordinator and distributing work to channels.
/// This is the central pattern: ONE SQL call (process_work_batch) → distribute to multiple channels.
/// Replaces direct calls to IWorkCoordinator.ProcessWorkBatchAsync() throughout the codebase.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkBatchCoordinatorTests.cs</tests>
public interface IWorkBatchCoordinator {
  /// <summary>
  /// Processes a work batch and distributes the results to appropriate channels.
  /// Calls IWorkCoordinator.ProcessWorkBatchAsync() and writes:
  /// - OutboxWork → IWorkChannelWriter
  /// - PerspectiveWork → IPerspectiveChannelWriter
  /// - InboxWork → (future: IInboxChannelWriter)
  /// </summary>
  /// <param name="instanceId">The service instance ID claiming work</param>
  /// <param name="outboxCompletions">Completed outbox messages from previous batch</param>
  /// <param name="outboxFailures">Failed outbox messages from previous batch</param>
  /// <param name="inboxCompletions">Completed inbox messages from previous batch</param>
  /// <param name="inboxFailures">Failed inbox messages from previous batch</param>
  /// <param name="perspectiveCompletions">Completed perspective checkpoints from previous batch</param>
  /// <param name="perspectiveFailures">Failed perspective checkpoints from previous batch</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>Task that completes when work is distributed to channels</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkBatchCoordinatorTests.cs:ProcessAndDistributeAsync_WithOutboxWork_WritesToOutboxChannelAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkBatchCoordinatorTests.cs:ProcessAndDistributeAsync_WithPerspectiveWork_WritesToPerspectiveChannelAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkBatchCoordinatorTests.cs:ProcessAndDistributeAsync_WithBothWorkTypes_DistributesToBothChannelsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkBatchCoordinatorTests.cs:ProcessAndDistributeAsync_WithCompletions_PassesToWorkCoordinatorAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkBatchCoordinatorTests.cs:ProcessAndDistributeAsync_WithMultipleOutboxWork_WritesAllToChannelAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkBatchCoordinatorTests.cs:ProcessAndDistributeAsync_WithMultiplePerspectiveWork_WritesAllToChannelAsync</tests>
  Task ProcessAndDistributeAsync(
    Guid instanceId,
    List<MessageCompletion>? outboxCompletions = null,
    List<MessageFailure>? outboxFailures = null,
    List<MessageCompletion>? inboxCompletions = null,
    List<MessageFailure>? inboxFailures = null,
    List<PerspectiveCheckpointCompletion>? perspectiveCompletions = null,
    List<PerspectiveCheckpointFailure>? perspectiveFailures = null,
    CancellationToken ct = default
  );
}
