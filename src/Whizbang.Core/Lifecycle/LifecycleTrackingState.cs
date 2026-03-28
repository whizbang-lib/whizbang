using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Per-event tracking state that drives lifecycle stage transitions.
/// Encapsulates receptor invocation and ImmediateAsync chaining.
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

    // Invoke receptors and tags at this stage
    await invoker.InvokeAsync(_envelope, stage, context, ct).ConfigureAwait(false);

    // ImmediateAsync fires after each stage
    await invoker.InvokeAsync(_envelope, LifecycleStage.ImmediateAsync,
      context with { CurrentStage = LifecycleStage.ImmediateAsync }, ct).ConfigureAwait(false);

    _recordStage(stage, stageStopwatch, startedAt);

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
