using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

/// <summary>Where a step currently stands, as the status surface reports it.</summary>
/// <docs>proposals/startup-pipeline#hooks</docs>
public enum StartupStepStatus {
  /// <summary>Not started — either the run has not reached it, or the name has never been seen.</summary>
  Pending,
  /// <summary>Executing right now.</summary>
  Running,
  /// <summary>Finished its work.</summary>
  Completed,
  /// <summary>Ran and deliberately did nothing; the result carries the reason.</summary>
  Skipped,
  /// <summary>Could not complete; the result carries the reason.</summary>
  Failed,
}

/// <summary>
/// Answers questions about the pipeline at any moment, for code that needs to make a decision
/// rather than watch a transition. <see cref="WaitForAsync"/> is what lets a consumer's own hosted
/// service say "after <c>Migrate</c>" instead of guessing at registration order.
/// </summary>
/// <docs>proposals/startup-pipeline#hooks</docs>
public interface IStartupPipelineState {
  /// <summary>Whether the current run has finished. False before any run and while one is underway.</summary>
  bool IsComplete { get; }

  /// <summary>Where <paramref name="stepName"/> currently stands. Unknown names report
  /// <see cref="StartupStepStatus.Pending"/> — the state is observational and cannot distinguish
  /// "not yet reached" from "never registered"; validation of names belongs at resolve time.</summary>
  StartupStepStatus StatusOf(string stepName);

  /// <summary>The steps that have finished so far this run, in completion order.</summary>
  IReadOnlyList<StartupStepResult> Completed { get; }

  /// <summary>Completes when <paramref name="stepName"/> finishes (whatever its outcome).
  /// Returns immediately if it already has this run.</summary>
  Task WaitForAsync(string stepName, CancellationToken cancellationToken);
}

/// <summary>
/// The in-memory pipeline state: an <see cref="IStartupStepObserver"/> that folds the transitions
/// it observes into the answers <see cref="IStartupPipelineState"/> promises. Registering it as an
/// observer is all the wiring there is — the state is derived from the same notifications every
/// other observer gets, never privileged.
/// </summary>
/// <remarks>
/// Re-entrant with the runner: a new run's first notification resets the previous run's answers,
/// because a reviving instance re-enters the pipeline and reporting the OLD run as complete would
/// tell it it is ready when it is not. Thread-safe; waiters registered before a step completes are
/// released when it does, and late waiters return immediately.
/// </remarks>
/// <docs>proposals/startup-pipeline#hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupPipelineHooksTests.cs</tests>
public sealed class StartupPipelineState : IStartupPipelineState, IStartupStepObserver {
  private readonly Lock _lock = new();
  private readonly Dictionary<string, StartupStepStatus> _statuses = new(StringComparer.Ordinal);
  private readonly Dictionary<string, TaskCompletionSource> _waiters = new(StringComparer.Ordinal);
  private readonly List<StartupStepResult> _completed = [];
  private bool _isComplete;
  private bool _runInProgress;

  /// <inheritdoc />
  public bool IsComplete {
    get { lock (_lock) { return _isComplete; } }
  }

  /// <inheritdoc />
  public StartupStepStatus StatusOf(string stepName) {
    ArgumentNullException.ThrowIfNull(stepName);
    lock (_lock) {
      return _statuses.TryGetValue(stepName, out var status) ? status : StartupStepStatus.Pending;
    }
  }

  /// <inheritdoc />
  public IReadOnlyList<StartupStepResult> Completed {
    get { lock (_lock) { return [.. _completed]; } }
  }

  /// <inheritdoc />
  public Task WaitForAsync(string stepName, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(stepName);
    TaskCompletionSource waiter;
    lock (_lock) {
      if (_statuses.TryGetValue(stepName, out var status)
          && status is StartupStepStatus.Completed or StartupStepStatus.Skipped or StartupStepStatus.Failed) {
        return Task.CompletedTask;
      }
      if (!_waiters.TryGetValue(stepName, out waiter!)) {
        waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[stepName] = waiter;
      }
    }
    return waiter.Task.WaitAsync(cancellationToken);
  }

  /// <inheritdoc />
  public ValueTask OnStepStartingAsync(StartupStepContext context, CancellationToken cancellationToken) {
    lock (_lock) {
      // The first notification of a new run resets the previous run's answers. Waiters are kept:
      // a dependent waiting on a step from before the re-entry is still waiting on that step —
      // the new run will complete it again.
      if (!_runInProgress) {
        _runInProgress = true;
        _isComplete = false;
        _statuses.Clear();
        _completed.Clear();
      }
      _statuses[context.Descriptor.Name] = StartupStepStatus.Running;
    }
    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask OnStepCompletedAsync(StartupStepResult result, CancellationToken cancellationToken) {
    TaskCompletionSource? waiter;
    lock (_lock) {
      _statuses[result.Name] = result.Outcome switch {
        StartupStepOutcome.Skipped => StartupStepStatus.Skipped,
        StartupStepOutcome.Failed => StartupStepStatus.Failed,
        _ => StartupStepStatus.Completed,
      };
      _completed.Add(result);
      _waiters.Remove(result.Name, out waiter);
    }
    waiter?.TrySetResult();
    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask OnPipelineCompletedAsync(StartupSummary summary, CancellationToken cancellationToken) {
    lock (_lock) {
      _isComplete = true;
      _runInProgress = false;
    }
    return ValueTask.CompletedTask;
  }
}
