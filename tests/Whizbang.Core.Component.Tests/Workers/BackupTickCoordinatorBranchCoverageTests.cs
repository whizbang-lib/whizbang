// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branches of <see cref="BackupTickCoordinator"/> the other coordinator tests leave untaken: the
/// options guard for a missing options wrapper or a wrapper with no value, and the polling loop's
/// own condition turning false (shutdown requested before the first iteration) rather than a
/// cancellation exception ending the loop.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/BackupTickCoordinator.cs</code-under-test>
[Category("Workers")]
public class BackupTickCoordinatorBranchCoverageTests {

  private sealed class NullValueOptions : IOptions<BackupTickCoordinatorOptions> {
    public BackupTickCoordinatorOptions Value => null!;
  }

  /// <summary>Returns ready, but cancels the host's stopping source first: a shutdown requested
  /// in the instant between schema readiness and the first loop iteration.</summary>
  private sealed class CancelOnReadySchemaGate(CancellationTokenSource stopping) : ISchemaReadyGate {
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsReady => true;
    public void MarkReady() { }
    public Task WaitForReadyAsync(CancellationToken cancellationToken) {
      stopping.Cancel();
      Entered.TrySetResult();
      return Task.CompletedTask;
    }
  }

  private sealed class CountingTracker : IIdleActivityTracker {
    private int _reads;
    public int Reads => Volatile.Read(ref _reads);
    public void Touch(string source) { }
    public TimeSpan TimeSinceLastActivity {
      get {
        Interlocked.Increment(ref _reads);
        return TimeSpan.MaxValue;
      }
    }
    public DateTimeOffset LastActivityAt => DateTimeOffset.UtcNow;
    public string LastActivitySource => string.Empty;
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new BackupTickCoordinator(
        new CountingTracker(),
        new BackupTickRegistry(),
        null!,
        NullLogger<BackupTickCoordinator>.Instance,
        SchemaReadyGate.AlreadyReady(),
        NullNotifySignalingGate.Instance))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new BackupTickCoordinator(
        new CountingTracker(),
        new BackupTickRegistry(),
        new NullValueOptions(),
        NullLogger<BackupTickCoordinator>.Instance,
        SchemaReadyGate.AlreadyReady(),
        NullNotifySignalingGate.Instance))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_StopRequestedBeforeFirstIteration_ExitsLoopWithoutReadingIdleTimeAsync(CancellationToken testToken) {
    using var stopping = new CancellationTokenSource();
    var schemaGate = new CancelOnReadySchemaGate(stopping);
    var tracker = new CountingTracker();
    var registry = new BackupTickRegistry();
    var ticked = 0;
    registry.Register("backstop", _ => { Interlocked.Increment(ref ticked); return Task.CompletedTask; }, () => true);

    var coordinator = new BackupTickCoordinator(
      tracker: tracker,
      registry: registry,
      options: Options.Create(new BackupTickCoordinatorOptions { IdleThreshold = TimeSpan.Zero }),
      logger: NullLogger<BackupTickCoordinator>.Instance,
      schemaReadyGate: schemaGate,
      gate: NullNotifySignalingGate.Instance);

    await coordinator.StartAsync(stopping.Token);
    await schemaGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    var executeTask = coordinator.ExecuteTask!;
    await executeTask.WaitAsync(TimeSpan.FromSeconds(10), testToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(executeTask.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the loop condition sees the stop request and exits normally; nothing throws");
    await Assert.That(tracker.Reads).IsEqualTo(0)
      .Because("a loop entered despite the stop request would read idle time before anything else");
    await Assert.That(coordinator.TotalTickCycles).IsEqualTo(0L);
    await Assert.That(Volatile.Read(ref ticked)).IsEqualTo(0);

    await coordinator.StopAsync(CancellationToken.None);
  }
}
