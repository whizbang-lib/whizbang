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

  // --- Test Doubles ---

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
