// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Branch coverage for <see cref="StartupReadyService"/>'s narrated wait: the backoff past its
/// first narration (probes 10, 30 and the steady-state every-60 arm), and the pending-step
/// description leaving out non-blocking and already-completed steps.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupReadiness.cs</code-under-test>
[Category("Startup")]
public class StartupReadyServiceBranchCoverageTests {

  private sealed class NarrationLogger : Microsoft.Extensions.Logging.ILogger<StartupReadyService> {
    private readonly List<string> _entries = [];
    private readonly Lock _lock = new();
    public IReadOnlyList<string> Entries {
      get {
        lock (_lock) {
          return [.. _entries];
        }
      }
    }
    IDisposable? Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) {
        _entries.Add(formatter(state, exception));
      }
    }
  }

  /// <summary>
  /// A pipeline state whose readiness completes when it has been described a set number of times.
  /// The service describes the pipeline only when it narrates, so this lets the test release the
  /// wait at an exact narration without any clock.
  /// </summary>
  private sealed class DescribeCountingPipelineState(int readyOnDescribe) : IStartupPipelineState {
    // Synchronous completion, and WaitForReadyAsync hands out the source's own task (no WaitAsync
    // wrapper): the wait is then observably complete on the very next probe after the release, so
    // the number of narrations is exact rather than racing a thread-pool continuation.
    private readonly TaskCompletionSource _ready = new();
    private int _describes;
    public int Describes => Volatile.Read(ref _describes);

    public bool IsComplete => _ready.Task.IsCompleted;
    public bool IsReady => _ready.Task.IsCompleted;
    public Task WaitForReadyAsync(CancellationToken cancellationToken) => _ready.Task;
    public StartupStepStatus StatusOf(string stepName) => StartupStepStatus.Pending;
    public IReadOnlyList<StartupStepResult> Completed => [];
    public bool HasRunStarted => true;
    public Task WaitForAsync(string stepName, CancellationToken cancellationToken) => Task.CompletedTask;

    public IReadOnlyList<StartupStepSnapshot> SnapshotSteps() {
      if (Interlocked.Increment(ref _describes) == readyOnDescribe) {
        _ready.TrySetResult();
      }
      return [
        new StartupStepSnapshot("Migrate", Blocking: true, StartupStepStatus.Running, null, null, null),
        new StartupStepSnapshot("Warmup", Blocking: false, StartupStepStatus.Pending, null, null, null),
        new StartupStepSnapshot("Reconcile", Blocking: true, StartupStepStatus.Completed, TimeSpan.Zero, StartupStepOutcome.Completed, null),
      ];
    }
  }

  /// <summary>
  /// A long wait keeps being narrated: probes 3, 10, 30 and then every 60, so the fourth
  /// narration is reached only through the steady-state arm. Each narration names only the
  /// blocking steps still outstanding: a non-blocking step does not hold readiness and a completed
  /// one is done, so naming either would send an operator after the wrong step.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task StartedAsync_LongPipelineWait_KeepsNarratingOnTheBackoffNamingOnlyOutstandingBlockingStepsAsync(
      CancellationToken cancellationToken) {
    var state = new DescribeCountingPipelineState(readyOnDescribe: 4);
    var signal = new StartupReadySignal();
    var logger = new NarrationLogger();
    var service = new StartupReadyService(pipelineState: state, signal: signal, contributors: [], logger: logger) {
      // Zero keeps the probe loop free of wall-clock time; the backoff counts probes, not time.
      WaitProbeInterval = TimeSpan.Zero,
    };

    await service.StartedAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);

    await Assert.That(signal.IsReady).IsTrue()
      .Because("the pipeline became ready during the fourth narration and there are no contributors");
    await Assert.That(state.Describes).IsEqualTo(4)
      .Because("the pipeline is described once per narration, and the fourth narration is the steady-state arm's");

    var narrations = logger.Entries.Where(e => e.Contains("still waiting on", StringComparison.Ordinal)).ToList();
    await Assert.That(narrations.Count).IsEqualTo(4);
    await Assert.That(narrations.All(n => n.Contains("startup pipeline step(s) Migrate (Running).", StringComparison.Ordinal))).IsTrue()
      .Because("the only outstanding blocking step is Migrate, and nothing else may be listed beside it");
    await Assert.That(narrations.Any(n => n.Contains("Warmup", StringComparison.Ordinal))).IsFalse()
      .Because("a non-blocking step never holds readiness");
    await Assert.That(narrations.Any(n => n.Contains("Reconcile", StringComparison.Ordinal))).IsFalse()
      .Because("a completed blocking step is not outstanding");
  }
}
