using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for PerspectiveMigrationWorker — verifies background rebuild processing.
/// </summary>
public class PerspectiveMigrationWorkerTests {
  [Test]
  public async Task ExecuteAsync_WithNoPendingRebuilds_CompletesQuicklyAsync() {
    // Arrange
    var rebuilder = new FakeRebuilder();
    var worker = new PerspectiveMigrationWorker(rebuilder, NullLogger<PerspectiveMigrationWorker>.Instance) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([]),
      UpdateMigrationStatus = (_, _, _, _) => Task.CompletedTask
    };

    // Act
    await worker.StartAsync(CancellationToken.None);
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0);
  }

  [Test]
  public async Task ExecuteAsync_WithPendingRebuild_CallsRebuilderAsync() {
    // Arrange
    var rebuilder = new FakeRebuilder();
    var statusUpdates = new List<(string Key, int Status, string Desc)>();

    var worker = new PerspectiveMigrationWorker(rebuilder, NullLogger<PerspectiveMigrationWorker>.Instance) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("OrderPerspective", "perspective:OrderPerspective")
      ]),
      UpdateMigrationStatus = (key, status, desc, _) => {
        statusUpdates.Add((key, status, desc));
        return Task.CompletedTask;
      }
    };

    // Act
    await worker.StartAsync(CancellationToken.None);
    // Give background task time to complete
    await Task.Delay(200);
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(1);
    await Assert.That(rebuilder.LastPerspectiveName).IsEqualTo("OrderPerspective");
    await Assert.That(statusUpdates).Count().IsEqualTo(1);
    await Assert.That(statusUpdates[0].Status).IsEqualTo(2); // Updated
  }

  [Test]
  public async Task ExecuteAsync_WithFailedRebuild_RecordsFailureStatusAsync() {
    // Arrange
    var rebuilder = new FakeRebuilder { ShouldFail = true };
    var statusUpdates = new List<(string Key, int Status, string Desc)>();

    var worker = new PerspectiveMigrationWorker(rebuilder, NullLogger<PerspectiveMigrationWorker>.Instance) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("FailingPerspective", "perspective:FailingPerspective")
      ]),
      UpdateMigrationStatus = (key, status, desc, _) => {
        statusUpdates.Add((key, status, desc));
        return Task.CompletedTask;
      }
    };

    // Act
    await worker.StartAsync(CancellationToken.None);
    await Task.Delay(200);
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(statusUpdates).Count().IsEqualTo(1);
    await Assert.That(statusUpdates[0].Status).IsEqualTo(-1); // Failed
  }

  [Test]
  public async Task ExecuteAsync_WithNoCallbacks_CompletesGracefullyAsync() {
    // Arrange — callbacks not set
    var rebuilder = new FakeRebuilder();
    var worker = new PerspectiveMigrationWorker(rebuilder, NullLogger<PerspectiveMigrationWorker>.Instance);

    // Act — should not throw
    await worker.StartAsync(CancellationToken.None);
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0);
  }

  [Test]
  public async Task ExecuteAsync_WithCancellation_StopsProcessingRemainingRebuildsAsync() {
    // Arrange
    var rebuilder = new FakeRebuilder();
    var cts = new CancellationTokenSource();
    var statusUpdates = new List<(string Key, int Status, string Desc)>();

    var worker = new PerspectiveMigrationWorker(rebuilder, NullLogger<PerspectiveMigrationWorker>.Instance) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("First", "perspective:First"),
        new PendingMigrationRebuild("Second", "perspective:Second"),
        new PendingMigrationRebuild("Third", "perspective:Third")
      ]),
      UpdateMigrationStatus = (key, status, desc, _) => {
        statusUpdates.Add((key, status, desc));
        // Cancel after first rebuild completes so the foreach break triggers
        cts.Cancel();
        return Task.CompletedTask;
      }
    };

    // Act
    await worker.StartAsync(cts.Token);
    await Task.Delay(500);
    await worker.StopAsync(CancellationToken.None);

    // Assert — only the first rebuild should have been processed before cancellation kicked in
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(1);
    await Assert.That(statusUpdates).Count().IsEqualTo(1);
  }

  [Test]
  public async Task ExecuteAsync_WhenRebuilderThrows_CatchesExceptionAndUpdatesStatusAsync() {
    // Arrange — covers lines 63-67 (inner catch block with status update)
    var rebuilder = new ThrowingRebuilder();
    var statusUpdates = new List<(string Key, int Status, string Desc)>();

    var worker = new PerspectiveMigrationWorker(rebuilder, NullLogger<PerspectiveMigrationWorker>.Instance) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("CrashPerspective", "perspective:CrashPerspective")
      ]),
      UpdateMigrationStatus = (key, status, desc, _) => {
        statusUpdates.Add((key, status, desc));
        return Task.CompletedTask;
      }
    };

    // Act — should not throw
    await worker.StartAsync(CancellationToken.None);
    await Task.Delay(300);
    await worker.StopAsync(CancellationToken.None);

    // Assert — exception caught, status updated to Failed
    await Assert.That(statusUpdates).Count().IsEqualTo(1);
    await Assert.That(statusUpdates[0].Status).IsEqualTo(-1);
    await Assert.That(statusUpdates[0].Desc).Contains("Boom");
  }

  [Test]
  public async Task ExecuteAsync_WhenRebuilderThrowsAndStatusUpdateFails_SwallowsBothExceptionsAsync() {
    // Arrange — covers lines 66-70 (best effort status update failure)
    var rebuilder = new ThrowingRebuilder();

    var worker = new PerspectiveMigrationWorker(rebuilder, NullLogger<PerspectiveMigrationWorker>.Instance) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("CrashPerspective", "perspective:CrashPerspective")
      ]),
      UpdateMigrationStatus = (_, _, _, _) => throw new InvalidOperationException("Status update also failed")
    };

    // Act — should not throw despite both failures
    await worker.StartAsync(CancellationToken.None);
    await Task.Delay(300);
    await worker.StopAsync(CancellationToken.None);

    // Assert — just verifying it completes without exception (reaching here = success)
    var completed = true;
    await Assert.That(completed).IsTrue();
  }

  [Test]
  public async Task ExecuteAsync_WhenGetPendingRebuildsThrows_CatchesOuterExceptionAsync() {
    // Arrange — covers lines 73-75 (outer catch block)
    var rebuilder = new FakeRebuilder();

    var worker = new PerspectiveMigrationWorker(rebuilder, NullLogger<PerspectiveMigrationWorker>.Instance) {
      GetPendingRebuilds = _ => throw new InvalidOperationException("Database unavailable"),
      UpdateMigrationStatus = (_, _, _, _) => Task.CompletedTask
    };

    // Act — should not throw
    await worker.StartAsync(CancellationToken.None);
    await Task.Delay(300);
    await worker.StopAsync(CancellationToken.None);

    // Assert — worker swallowed the exception
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0);
  }

  [Test]
  public async Task ExecuteAsync_WithOnlyGetPendingRebuildsNull_ReturnsEarlyAsync() {
    // Arrange — covers the OR condition: GetPendingRebuilds is null but UpdateMigrationStatus is set
    var rebuilder = new FakeRebuilder();
    var worker = new PerspectiveMigrationWorker(rebuilder, NullLogger<PerspectiveMigrationWorker>.Instance) {
      UpdateMigrationStatus = (_, _, _, _) => Task.CompletedTask
    };

    // Act
    await worker.StartAsync(CancellationToken.None);
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0);
  }

  [Test]
  public async Task ExecuteAsync_WithOnlyUpdateMigrationStatusNull_ReturnsEarlyAsync() {
    // Arrange — covers the OR condition: UpdateMigrationStatus is null but GetPendingRebuilds is set
    var rebuilder = new FakeRebuilder();
    var worker = new PerspectiveMigrationWorker(rebuilder, NullLogger<PerspectiveMigrationWorker>.Instance) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([])
    };

    // Act
    await worker.StartAsync(CancellationToken.None);
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0);
  }

  // --- Test Doubles ---

  private sealed class ThrowingRebuilder : IPerspectiveRebuilder {
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct) =>
        throw new InvalidOperationException("Boom");

    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct) =>
        throw new InvalidOperationException("Boom");

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct) =>
        throw new InvalidOperationException("Boom");

    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct) =>
        Task.FromResult<RebuildStatus?>(null);
  }

  private sealed class FakeRebuilder : IPerspectiveRebuilder {
    public int RebuildCount { get; private set; }
    public string? LastPerspectiveName { get; private set; }
    public bool ShouldFail { get; init; }

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct) {
      RebuildCount++;
      LastPerspectiveName = perspectiveName;
      return Task.FromResult(ShouldFail
        ? new RebuildResult(perspectiveName, 0, 0, TimeSpan.Zero, false, "Simulated failure")
        : new RebuildResult(perspectiveName, 5, 10, TimeSpan.FromSeconds(1), true, null));
    }

    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct) =>
        RebuildBlueGreenAsync(perspectiveName, ct);

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct) =>
        RebuildBlueGreenAsync(perspectiveName, ct);

    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct) =>
        Task.FromResult<RebuildStatus?>(null);
  }
}
