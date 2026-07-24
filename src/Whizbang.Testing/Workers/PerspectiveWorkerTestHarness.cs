using System.Collections.Concurrent;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Testing.Workers;

/// <summary>
/// Channel-mode test harness for PerspectiveWorker. After the legacy poll path was deleted
/// (commit C of the work-pump decomposition), tests that exercise the worker must wire its
/// channel surfaces. This harness provides default capturing implementations so tests focus
/// on assertions instead of plumbing.
/// </summary>
/// <remarks>
/// Pair with <see cref="WorkCoordinatorPumpAdapter"/> when you have a fake IWorkCoordinator
/// that returns work via ProcessWorkBatchAsync — the adapter pumps that work through the
/// harness channel, simulating what ClaimWorker does in production.
/// </remarks>
/// <docs>fundamentals/work-coordinator/handler-commit</docs>
public sealed class PerspectiveWorkerTestHarness {
  /// <summary>Producer side of the perspective work channel.</summary>
  public PerspectiveChannelWriter ChannelWriter { get; } = new();

  /// <summary>Drain-mode channel for forwarded SQL-detected stream IDs.</summary>
  public PerspectiveDrainChannel DrainChannel { get; } = new();

  /// <summary>Captures completions enqueued by the worker.</summary>
  public CapturingPerspectiveCompletionChannel CompletionCapture { get; } = new();

  /// <summary>Captures failures enqueued by the worker.</summary>
  public CapturingFailureChannel FailureCapture { get; } = new();

  /// <summary>Captures lease renewals enqueued by the worker.</summary>
  public CapturingLeaseRenewalChannel LeaseRenewalCapture { get; } = new();

  /// <summary>Enqueue per-event perspective work to be processed by the worker.</summary>
  public ValueTask EnqueueWorkAsync(PerspectiveWork work, CancellationToken ct = default)
    => ChannelWriter.WriteAsync(work, ct);

  /// <summary>Enqueue a drain-mode stream id (batched RunWithEventsAsync path).</summary>
  public ValueTask EnqueueDrainStreamAsync(Guid streamId, CancellationToken ct = default)
    => DrainChannel.WriteAsync(streamId, ct);
}

/// <summary>
/// In-memory IPerspectiveCompletionChannel that captures every cursor + event-work-id
/// the worker enqueues for assertions.
/// </summary>
public sealed class CapturingPerspectiveCompletionChannel : IPerspectiveCompletionChannel {
  private readonly TaskCompletionSource _firstEventWorkId = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private readonly TaskCompletionSource _firstCursor = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Captured event work IDs (perspective_event row deletions).</summary>
  public ConcurrentQueue<Guid> EventWorkIds { get; } = new();

  /// <summary>Captured cursor advancements.</summary>
  public ConcurrentQueue<PerspectiveCursorCompletion> Cursors { get; } = new();

  /// <summary>Completes when the first event-work-id row deletion is enqueued — a deterministic
  /// signal for tests that assert the completion flush ran (vs. racing the worker's idle tick).</summary>
  public Task FirstEventWorkId => _firstEventWorkId.Task;

  /// <summary>Completes when the first cursor advancement is enqueued.</summary>
  public Task FirstCursor => _firstCursor.Task;

  /// <inheritdoc />
  public ValueTask EnqueueEventWorkIdAsync(Guid eventWorkId, CancellationToken cancellationToken = default) {
    EventWorkIds.Enqueue(eventWorkId);
    _firstEventWorkId.TrySetResult();
    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask EnqueueCursorAsync(PerspectiveCursorCompletion cursor, CancellationToken cancellationToken = default) {
    Cursors.Enqueue(cursor);
    _firstCursor.TrySetResult();
    return ValueTask.CompletedTask;
  }
}

/// <summary>In-memory IFailureChannel that captures every failure for assertions.</summary>
public sealed class CapturingFailureChannel : IFailureChannel {
  /// <summary>Captured (category, failure) tuples.</summary>
  public ConcurrentQueue<(WorkCategory category, MessageFailure failure)> Items { get; } = new();

  /// <inheritdoc />
  public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
    Items.Enqueue((category, failure));
    return ValueTask.CompletedTask;
  }
}

/// <summary>In-memory ILeaseRenewalChannel that captures every renewal for assertions.</summary>
public sealed class CapturingLeaseRenewalChannel : ILeaseRenewalChannel {
  /// <summary>Captured (category, id) tuples.</summary>
  public ConcurrentQueue<(WorkCategory category, Guid id)> Items { get; } = new();

  /// <inheritdoc />
  public ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken cancellationToken = default) {
    Items.Enqueue((category, id));
    return ValueTask.CompletedTask;
  }
}
