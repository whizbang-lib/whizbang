using System.Text.Json;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for IPerspectiveCompletionStrategy implementations.
/// Tests define the expected behavior before implementation (TDD RED phase).
/// </summary>
public class PerspectiveCompletionStrategyTests {
  #region BatchedCompletionStrategy Tests

  [Test]
  public async Task BatchedStrategy_ReportCompletionAsync_DoesNotCallCoordinatorImmediately_Async() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var completion = new PerspectiveCheckpointCompletion {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Completed
    };

    var strategy = new BatchedCompletionStrategy();

    // Act
    await strategy.ReportCompletionAsync(completion, coordinator, CancellationToken.None);

    // Assert - coordinator should NOT be called (batched for next cycle)
    await Assert.That(coordinator.ReportCompletionCallCount).IsEqualTo(0);
    await Assert.That(coordinator.ReportFailureCallCount).IsEqualTo(0);
  }

  [Test]
  public async Task BatchedStrategy_ReportFailureAsync_DoesNotCallCoordinatorImmediately_Async() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var failure = new PerspectiveCheckpointFailure {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Failed,
      Error = "Test error"
    };

    var strategy = new BatchedCompletionStrategy();

    // Act
    await strategy.ReportFailureAsync(failure, coordinator, CancellationToken.None);

    // Assert - coordinator should NOT be called (batched for next cycle)
    await Assert.That(coordinator.ReportCompletionCallCount).IsEqualTo(0);
    await Assert.That(coordinator.ReportFailureCallCount).IsEqualTo(0);
  }

  [Test]
  public async Task BatchedStrategy_GetPendingCompletions_ReturnsCollectedCompletions_Async() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var completion1 = new PerspectiveCheckpointCompletion {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "Perspective1",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Completed
    };
    var completion2 = new PerspectiveCheckpointCompletion {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "Perspective2",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Completed
    };

    var strategy = new BatchedCompletionStrategy();

    // Act - report two completions
    await strategy.ReportCompletionAsync(completion1, coordinator, CancellationToken.None);
    await strategy.ReportCompletionAsync(completion2, coordinator, CancellationToken.None);

    var pending = strategy.GetPendingCompletions();

    // Assert
    await Assert.That(pending).Count().IsEqualTo(2);
    await Assert.That(pending).Contains(completion1);
    await Assert.That(pending).Contains(completion2);
  }

  [Test]
  public async Task BatchedStrategy_GetPendingFailures_ReturnsCollectedFailures_Async() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var failure1 = new PerspectiveCheckpointFailure {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "Perspective1",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Failed,
      Error = "Error 1"
    };
    var failure2 = new PerspectiveCheckpointFailure {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "Perspective2",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Failed,
      Error = "Error 2"
    };

    var strategy = new BatchedCompletionStrategy();

    // Act - report two failures
    await strategy.ReportFailureAsync(failure1, coordinator, CancellationToken.None);
    await strategy.ReportFailureAsync(failure2, coordinator, CancellationToken.None);

    var pending = strategy.GetPendingFailures();

    // Assert
    await Assert.That(pending).Count().IsEqualTo(2);
    await Assert.That(pending).Contains(failure1);
    await Assert.That(pending).Contains(failure2);
  }

  [Test]
  public async Task BatchedStrategy_ClearPending_RemovesAllCollectedItems_Async() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var completion = new PerspectiveCheckpointCompletion {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Completed
    };
    var failure = new PerspectiveCheckpointFailure {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Failed,
      Error = "Test error"
    };

    var strategy = new BatchedCompletionStrategy();
    await strategy.ReportCompletionAsync(completion, coordinator, CancellationToken.None);
    await strategy.ReportFailureAsync(failure, coordinator, CancellationToken.None);

    // Act
    strategy.ClearPending();

    // Assert
    await Assert.That(strategy.GetPendingCompletions()).IsEmpty();
    await Assert.That(strategy.GetPendingFailures()).IsEmpty();
  }

  #endregion

  #region InstantCompletionStrategy Tests

  [Test]
  public async Task InstantStrategy_ReportCompletionAsync_CallsCoordinatorImmediately_Async() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var completion = new PerspectiveCheckpointCompletion {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Completed
    };

    var strategy = new InstantCompletionStrategy();

    // Act
    await strategy.ReportCompletionAsync(completion, coordinator, CancellationToken.None);

    // Assert - coordinator SHOULD be called immediately
    await Assert.That(coordinator.ReportCompletionCallCount).IsEqualTo(1);
    await Assert.That(coordinator.LastCompletion).IsEqualTo(completion);
  }

  [Test]
  public async Task InstantStrategy_ReportFailureAsync_CallsCoordinatorImmediately_Async() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var failure = new PerspectiveCheckpointFailure {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "TestPerspective",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Failed,
      Error = "Test error"
    };

    var strategy = new InstantCompletionStrategy();

    // Act
    await strategy.ReportFailureAsync(failure, coordinator, CancellationToken.None);

    // Assert - coordinator SHOULD be called immediately
    await Assert.That(coordinator.ReportFailureCallCount).IsEqualTo(1);
    await Assert.That(coordinator.LastFailure).IsEqualTo(failure);
  }

  [Test]
  public async Task InstantStrategy_GetPendingCompletions_AlwaysReturnsEmpty_Async() {
    // Arrange
    var strategy = new InstantCompletionStrategy();

    // Act
    var pending = strategy.GetPendingCompletions();

    // Assert - nothing should be pending (instant strategy never batches)
    await Assert.That(pending).IsEmpty();
  }

  [Test]
  public async Task InstantStrategy_GetPendingFailures_AlwaysReturnsEmpty_Async() {
    // Arrange
    var strategy = new InstantCompletionStrategy();

    // Act
    var pending = strategy.GetPendingFailures();

    // Assert - nothing should be pending (instant strategy never batches)
    await Assert.That(pending).IsEmpty();
  }

  #endregion

  #region Test Fakes

  /// <summary>
  /// Fake IWorkCoordinator for testing strategy behavior
  /// </summary>
  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public int ReportCompletionCallCount { get; private set; }
    public int ReportFailureCallCount { get; private set; }
    public PerspectiveCheckpointCompletion? LastCompletion { get; private set; }
    public PerspectiveCheckpointFailure? LastFailure { get; private set; }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      Guid instanceId,
      string serviceName,
      string hostName,
      int processId,
      Dictionary<string, JsonElement>? metadata,
      MessageCompletion[] outboxCompletions,
      MessageFailure[] outboxFailures,
      MessageCompletion[] inboxCompletions,
      MessageFailure[] inboxFailures,
      ReceptorProcessingCompletion[] receptorCompletions,
      ReceptorProcessingFailure[] receptorFailures,
      PerspectiveCheckpointCompletion[] perspectiveCompletions,
      PerspectiveCheckpointFailure[] perspectiveFailures,
      OutboxMessage[] newOutboxMessages,
      InboxMessage[] newInboxMessages,
      Guid[] renewOutboxLeaseIds,
      Guid[] renewInboxLeaseIds,
      WorkBatchFlags flags = WorkBatchFlags.None,
      int partitionCount = 10000,
      int leaseSeconds = 300,
      int staleThresholdSeconds = 600,
      CancellationToken cancellationToken = default) {
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCheckpointCompletion completion,
      CancellationToken cancellationToken = default) {
      ReportCompletionCallCount++;
      LastCompletion = completion;
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCheckpointFailure failure,
      CancellationToken cancellationToken = default) {
      ReportFailureCallCount++;
      LastFailure = failure;
      return Task.CompletedTask;
    }
  }

  #endregion
}
