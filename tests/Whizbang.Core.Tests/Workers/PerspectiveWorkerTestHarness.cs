using System.Collections.Concurrent;
using System.Threading.Channels;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Channel-mode test harness for PerspectiveWorker. After commit C deleted the legacy
/// poll path, every test that exercises the worker must wire the channel surfaces.
/// This harness provides default capturing implementations so tests focus on assertions
/// instead of plumbing.
/// </summary>
internal sealed class PerspectiveWorkerTestHarness {
  public PerspectiveChannelWriter ChannelWriter { get; } = new();
  public PerspectiveDrainChannel DrainChannel { get; } = new();
  public CapturingPerspectiveCompletionChannel CompletionCapture { get; } = new();
  public CapturingFailureChannel FailureCapture { get; } = new();
  public CapturingLeaseRenewalChannel LeaseRenewalCapture { get; } = new();

  /// <summary>Enqueue per-event perspective work to be processed by the worker.</summary>
  public ValueTask EnqueueWorkAsync(PerspectiveWork work, CancellationToken ct = default)
    => ChannelWriter.WriteAsync(work, ct);

  /// <summary>Enqueue a drain-mode stream id (batched RunWithEventsAsync path).</summary>
  public ValueTask EnqueueDrainStreamAsync(Guid streamId, CancellationToken ct = default)
    => DrainChannel.WriteAsync(streamId, ct);

  /// <summary>Wait for any cursor completion to land on the completion channel.
  /// Signal-based (SemaphoreSlim released on enqueue) — no polling.</summary>
  public async Task<PerspectiveCursorCompletion> WaitForCompletionAsync(TimeSpan timeout) {
    await CompletionCapture.WaitForAnyCursorAsync(timeout);
    CompletionCapture.Cursors.TryPeek(out var first);
    return first!;
  }

  /// <summary>Wait for any event-work-id deletion to land on the completion channel.
  /// Signal-based (SemaphoreSlim released on enqueue) — no polling.</summary>
  public async Task<Guid> WaitForEventWorkIdAsync(TimeSpan timeout) {
    await CompletionCapture.WaitForAnyEventWorkIdAsync(timeout);
    CompletionCapture.EventWorkIds.TryPeek(out var first);
    return first;
  }
}

internal sealed class CapturingPerspectiveCompletionChannel : IPerspectiveCompletionChannel, IDisposable {
  private readonly SemaphoreSlim _eventWorkIdSignal = new(0, int.MaxValue);
  private readonly SemaphoreSlim _cursorSignal = new(0, int.MaxValue);
  public ConcurrentQueue<Guid> EventWorkIds { get; } = new();
  public ConcurrentQueue<PerspectiveCursorCompletion> Cursors { get; } = new();
  public ValueTask EnqueueEventWorkIdAsync(Guid eventWorkId, CancellationToken cancellationToken = default) {
    EventWorkIds.Enqueue(eventWorkId);
    _eventWorkIdSignal.Release();
    return ValueTask.CompletedTask;
  }
  public ValueTask EnqueueCursorAsync(PerspectiveCursorCompletion cursor, CancellationToken cancellationToken = default) {
    Cursors.Enqueue(cursor);
    _cursorSignal.Release();
    return ValueTask.CompletedTask;
  }

  /// <summary>Completes when at least one cursor completion has been enqueued. Fast-paths when
  /// the queue is already non-empty so repeated waits keep the original peek semantics.</summary>
  public async Task WaitForAnyCursorAsync(TimeSpan timeout) {
    if (!Cursors.IsEmpty) {
      return;
    }
    if (!await _cursorSignal.WaitAsync(timeout)) {
      throw new TimeoutException($"No completion seen within {timeout}");
    }
  }

  /// <summary>Completes when at least one event-work-id has been enqueued. Fast-paths when
  /// the queue is already non-empty so repeated waits keep the original peek semantics.</summary>
  public async Task WaitForAnyEventWorkIdAsync(TimeSpan timeout) {
    if (!EventWorkIds.IsEmpty) {
      return;
    }
    if (!await _eventWorkIdSignal.WaitAsync(timeout)) {
      throw new TimeoutException($"No event-work-id seen within {timeout}");
    }
  }

  public void Dispose() {
    _eventWorkIdSignal.Dispose();
    _cursorSignal.Dispose();
  }
}

internal sealed class CapturingFailureChannel : IFailureChannel {
  public ConcurrentQueue<(WorkCategory category, MessageFailure failure)> Items { get; } = new();
  public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
    Items.Enqueue((category, failure));
    return ValueTask.CompletedTask;
  }
}

internal sealed class CapturingLeaseRenewalChannel : ILeaseRenewalChannel {
  public ConcurrentQueue<(WorkCategory category, Guid id)> Items { get; } = new();
  public ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken cancellationToken = default) {
    Items.Enqueue((category, id));
    return ValueTask.CompletedTask;
  }
}
