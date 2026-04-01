using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Per-event tracking state that drives lifecycle stage transitions.
/// Encapsulates receptor invocation and ImmediateDetached chaining.
/// </summary>
/// <remarks>
/// Thread-safe via <see cref="Lock"/> for stage transitions.
/// The <see cref="AdvanceToAsync"/> method resolves <see cref="IReceptorInvoker"/>
/// from the scoped provider and delegates invocation.
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator#tracking-state</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleCoordinatorTests.cs</tests>
internal sealed class LifecycleTrackingState : ILifecycleTracking {
  private readonly IMessageEnvelope _envelope;
  private readonly MessageSource _source;
  private readonly Guid? _streamId;
  private readonly Type? _perspectiveType;
  private readonly DebugAwareStopwatch _totalStopwatch;
  private readonly List<StageRecord> _stageHistory = [];
  private readonly HashSet<LifecycleStage> _firedStages = [];
  private readonly List<Task> _detachedTasks = [];
  private readonly Lock _lock = new();
  private LifecycleStage _currentStage;
  private bool _isComplete;
  private DateTimeOffset _lastActivityUtc;

  /// <summary>
  /// Initializes a new instance of the <see cref="LifecycleTrackingState"/> class.
  /// </summary>
  /// <param name="eventId">The event ID being tracked.</param>
  /// <param name="envelope">The message envelope for receptor invocation.</param>
  /// <param name="entryStage">The lifecycle stage at which tracking begins.</param>
  /// <param name="source">Whether the message arrived via inbox, local dispatch, etc.</param>
  /// <param name="streamId">The optional stream ID for stream-scoped lifecycle.</param>
  /// <param name="perspectiveType">The optional perspective type for perspective-scoped lifecycle.</param>
  public LifecycleTrackingState(
    Guid eventId,
    IMessageEnvelope envelope,
    LifecycleStage entryStage,
    MessageSource source,
    Guid? streamId,
    Type? perspectiveType) {
    EventId = eventId;
    _envelope = envelope;
    _currentStage = entryStage;
    _source = source;
    _streamId = streamId;
    _perspectiveType = perspectiveType;
    _totalStopwatch = DebugAwareStopwatch.StartNew();
    _lastActivityUtc = DateTimeOffset.UtcNow;
  }

  /// <inheritdoc/>
  public Guid EventId { get; }

  /// <inheritdoc/>
  public LifecycleStage CurrentStage {
    get {
      lock (_lock) {
        return _currentStage;
      }
    }
  }

  /// <inheritdoc/>
  public bool IsComplete {
    get {
      lock (_lock) {
        return _isComplete;
      }
    }
  }

  /// <summary>
  /// Last time any activity occurred on this tracking instance.
  /// Used for debounce-style stale tracking cleanup — resets on every stage transition
  /// and perspective signal, creating a sliding inactivity window.
  /// </summary>
  public DateTimeOffset LastActivityUtc {
    get {
      lock (_lock) {
        return _lastActivityUtc;
      }
    }
  }

  /// <summary>
  /// Resets the inactivity timer. Called when a perspective signals completion
  /// to keep the tracking alive while perspectives are still trickling in.
  /// </summary>
  internal void TouchActivity() {
    lock (_lock) {
      _lastActivityUtc = DateTimeOffset.UtcNow;
    }
  }

  /// <summary>
  /// Waits for all in-flight detached tasks to complete.
  /// Used for graceful shutdown and testing — production callers should not need this.
  /// </summary>
  public async ValueTask DrainDetachedAsync() {
    Task[] tasks;
    lock (_lock) {
      tasks = [.. _detachedTasks];
    }
    await Task.WhenAll(tasks).ConfigureAwait(false);
  }

  /// <summary>
  /// Returns a snapshot of in-flight detached tasks.
  /// Used by the coordinator to collect tasks from abandoned trackings.
  /// </summary>
  internal Task[] GetDetachedTasks() {
    lock (_lock) {
      return [.. _detachedTasks];
    }
  }

  /// <inheritdoc/>
  public async ValueTask AdvanceToAsync(
    LifecycleStage stage,
    IServiceProvider scopedProvider,
    CancellationToken ct) {
    var stageStopwatch = DebugAwareStopwatch.StartNew();
    var startedAt = DateTimeOffset.UtcNow;

    lock (_lock) {
      // Stage guard: each stage fires at most once per event (exactly-once guarantee)
      if (_isComplete || !_firedStages.Add(stage)) {
        return;
      }
      _currentStage = stage;
      _lastActivityUtc = DateTimeOffset.UtcNow;
    }

    // Resolve scoped invoker
    var invoker = scopedProvider.GetService<IReceptorInvoker>();
    if (invoker is null) {
      _recordStage(stage, stageStopwatch, startedAt);
      return;
    }

    var context = new LifecycleExecutionContext {
      CurrentStage = stage,
      EventId = EventId,
      StreamId = _streamId,
      PerspectiveType = _perspectiveType,
      MessageSource = _source,
      AttemptNumber = 1
    };

    if (stage.IsDetached()) {
      // Detached stages: fire-and-forget with own DI scope.
      // The receptor runs independently — if it calls WaitForStreamAsync, it blocks its own task, not the pipeline.
      _recordStage(stage, stageStopwatch, startedAt);
      var scopeFactory = scopedProvider.GetRequiredService<IServiceScopeFactory>();
      var detachedTask = Task.Run(async () => {
        try {
          await using var detachedScope = scopeFactory.CreateAsyncScope();
          await SecurityContextHelper.EstablishFullContextAsync(_envelope, detachedScope.ServiceProvider, ct);
          var detachedInvoker = detachedScope.ServiceProvider.GetService<IReceptorInvoker>();
          if (detachedInvoker is null) {
            return;
          }
          await detachedInvoker.InvokeAsync(_envelope, stage, context, ct).ConfigureAwait(false);
          await detachedInvoker.InvokeAsync(_envelope, LifecycleStage.ImmediateDetached,
            context with { CurrentStage = LifecycleStage.ImmediateDetached }, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
          // Graceful shutdown
#pragma warning disable RCS1075 // No logger available in tracking state; errors surface via receptor telemetry
        } catch (Exception) {
#pragma warning restore RCS1075
          // Errors surface via receptor telemetry — no logger available in tracking state
        }
      }, ct);
      lock (_lock) {
        _detachedTasks.Add(detachedTask);
      }
    } else {
      // Inline stages: await directly — blocks the pipeline until all receptors complete
      await invoker.InvokeAsync(_envelope, stage, context, ct).ConfigureAwait(false);

      // ImmediateDetached fires after each Inline stage (awaited, part of the blocking pipeline)
      await invoker.InvokeAsync(_envelope, LifecycleStage.ImmediateDetached,
        context with { CurrentStage = LifecycleStage.ImmediateDetached }, ct).ConfigureAwait(false);

      _recordStage(stage, stageStopwatch, startedAt);
    }

    // Mark complete after PostLifecycleInline
    if (stage == LifecycleStage.PostLifecycleInline) {
      lock (_lock) {
        _isComplete = true;
      }
      _totalStopwatch.Stop();
    }
  }

  private void _recordStage(LifecycleStage stage, DebugAwareStopwatch stageStopwatch, DateTimeOffset startedAt) {
    stageStopwatch.Stop();
    lock (_lock) {
      _stageHistory.Add(new StageRecord(stage, stageStopwatch.Elapsed, startedAt));
    }
  }
}
