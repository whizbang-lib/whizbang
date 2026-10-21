// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

// Types for DestructionStageReceptorComponentTests (#1381) and StreamCloserDestructionStageComponentTests (#1504). An event with a receptor at each destruction stage, each
// recording the stage per event id. The counters are [ReceptorIdempotent] so the double-fire guard lets every call through.

namespace Whizbang.Core.Component.Tests.DestructionStages;

public static class DestructionProbe {
  private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<LifecycleStage>> _stages = new();
  private static readonly ConcurrentDictionary<(Guid, LifecycleStage), TaskCompletionSource> _signals = new();

  public static void Record(Guid id, LifecycleStage stage) {
    _stages.GetOrAdd(id, static _ => new()).Enqueue(stage);
    _signal(id, stage).TrySetResult();
  }

  public static LifecycleStage[] Stages(Guid id) => _stages.TryGetValue(id, out var q) ? [.. q] : [];

  /// <summary>Completes when <paramref name="stage"/> has run for <paramref name="id"/> (Detached stages run in the background).</summary>
  public static Task RanAsync(Guid id, LifecycleStage stage) => _signal(id, stage).Task;

  private static TaskCompletionSource _signal(Guid id, LifecycleStage stage) =>
    _signals.GetOrAdd((id, stage), static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

  private static readonly ConcurrentDictionary<Guid, bool> _failPreInline = new();

  /// <summary>Makes the Pre Inline receptor throw for <paramref name="id"/>, as a failing preserve step would.</summary>
  public static void FailPreInlineFor(Guid id) => _failPreInline[id] = true;

  public static bool ShouldFailPreInline(Guid id) => _failPreInline.ContainsKey(id);

  private static readonly ConcurrentDictionary<Guid, bool> _failPreDetached = new();

  /// <summary>Makes the Pre Detached receptor throw for <paramref name="id"/>.</summary>
  public static void FailPreDetachedFor(Guid id) => _failPreDetached[id] = true;

  public static bool ShouldFailPreDetached(Guid id) => _failPreDetached.ContainsKey(id);

  private static readonly ConcurrentDictionary<Guid, bool> _failPostInline = new();

  /// <summary>Makes the Post Inline receptor throw for <paramref name="id"/>, after the data is already gone.</summary>
  public static void FailPostInlineFor(Guid id) => _failPostInline[id] = true;

  public static bool ShouldFailPostInline(Guid id) => _failPostInline.ContainsKey(id);

  private static readonly ConcurrentDictionary<(Guid, LifecycleStage), bool> _cancel = new();

  /// <summary>Makes the Pre Inline receptor throw <see cref="OperationCanceledException"/> for <paramref name="id"/>, as a shutdown would.</summary>
  public static void CancelPreInlineFor(Guid id) => _cancel[(id, LifecycleStage.PreDestructionInline)] = true;

  /// <summary>Makes the Post Inline receptor throw <see cref="OperationCanceledException"/> for <paramref name="id"/>.</summary>
  public static void CancelPostInlineFor(Guid id) => _cancel[(id, LifecycleStage.PostDestructionInline)] = true;

  /// <summary>Throws <see cref="OperationCanceledException"/> when <paramref name="stage"/> was set to cancel for <paramref name="id"/>.</summary>
  public static void ThrowIfCanceled(Guid id, LifecycleStage stage) {
    if (_cancel.ContainsKey((id, stage))) {
      throw new OperationCanceledException("the destruction stage was canceled");
    }
  }
}

public record DsEphemeralEvent([property: StreamId] Guid Id) : IEvent;

[FireAt(LifecycleStage.PreDestructionDetached)]
[ReceptorIdempotent]
public sealed class DsPreDetached : IReceptor<DsEphemeralEvent> {
  public ValueTask HandleAsync(DsEphemeralEvent message, CancellationToken cancellationToken = default) {
    if (DestructionProbe.ShouldFailPreDetached(message.Id)) {
      throw new InvalidOperationException("the detached notification failed");
    }
    DestructionProbe.Record(message.Id, LifecycleStage.PreDestructionDetached);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PreDestructionInline)]
[ReceptorIdempotent]
public sealed class DsPreInline : IReceptor<DsEphemeralEvent> {
  public ValueTask HandleAsync(DsEphemeralEvent message, CancellationToken cancellationToken = default) {
    DestructionProbe.ThrowIfCanceled(message.Id, LifecycleStage.PreDestructionInline);
    if (DestructionProbe.ShouldFailPreInline(message.Id)) {
      throw new InvalidOperationException("the preserve step failed");
    }
    DestructionProbe.Record(message.Id, LifecycleStage.PreDestructionInline);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostDestructionDetached)]
[ReceptorIdempotent]
public sealed class DsPostDetached : IReceptor<DsEphemeralEvent> {
  public ValueTask HandleAsync(DsEphemeralEvent message, CancellationToken cancellationToken = default) {
    DestructionProbe.Record(message.Id, LifecycleStage.PostDestructionDetached);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostDestructionInline)]
[ReceptorIdempotent]
public sealed class DsPostInline : IReceptor<DsEphemeralEvent> {
  public ValueTask HandleAsync(DsEphemeralEvent message, CancellationToken cancellationToken = default) {
    DestructionProbe.ThrowIfCanceled(message.Id, LifecycleStage.PostDestructionInline);
    if (DestructionProbe.ShouldFailPostInline(message.Id)) {
      throw new InvalidOperationException("the post-destruction notification failed");
    }
    DestructionProbe.Record(message.Id, LifecycleStage.PostDestructionInline);
    return ValueTask.CompletedTask;
  }
}

/// <summary>An event no destruction receptor handles.</summary>
public record DsUnwatchedEvent([property: StreamId] Guid Id) : IEvent;
