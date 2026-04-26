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

  /// <summary>Wait for any cursor completion to land on the completion channel.</summary>
  public async Task<PerspectiveCursorCompletion> WaitForCompletionAsync(TimeSpan timeout) {
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline) {
      if (CompletionCapture.Cursors.TryPeek(out var first)) {
        return first;
      }
      await Task.Delay(10);
    }
    throw new TimeoutException($"No completion seen within {timeout}");
  }

  /// <summary>Wait for any event-work-id deletion to land on the completion channel.</summary>
  public async Task<Guid> WaitForEventWorkIdAsync(TimeSpan timeout) {
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline) {
      if (CompletionCapture.EventWorkIds.TryPeek(out var first)) {
        return first;
      }
      await Task.Delay(10);
    }
    throw new TimeoutException($"No event-work-id seen within {timeout}");
  }
}

internal sealed class CapturingPerspectiveCompletionChannel : IPerspectiveCompletionChannel {
  public ConcurrentQueue<Guid> EventWorkIds { get; } = new();
  public ConcurrentQueue<PerspectiveCursorCompletion> Cursors { get; } = new();
  public ValueTask EnqueueEventWorkIdAsync(Guid eventWorkId, CancellationToken cancellationToken = default) {
    EventWorkIds.Enqueue(eventWorkId);
    return ValueTask.CompletedTask;
  }
  public ValueTask EnqueueCursorAsync(PerspectiveCursorCompletion cursor, CancellationToken cancellationToken = default) {
    Cursors.Enqueue(cursor);
    return ValueTask.CompletedTask;
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
