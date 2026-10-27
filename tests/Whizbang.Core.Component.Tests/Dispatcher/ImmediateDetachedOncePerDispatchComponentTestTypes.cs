// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

// Types for ImmediateDetachedOncePerDispatchComponentTests (#1386). Each command has a business
// receptor and an [FireAt(ImmediateDetached)] receptor that counts its invocations per command id.
// The counters are [ReceptorIdempotent], so the double-fire guard lets every invocation through:
// the counts are the calls the framework makes, not what the guard leaves of them. A receptor that
// declares itself idempotent (a metrics or activity hook usually does) sees exactly those calls.
// The source generator discovers every receptor here, so the tests run the REAL generated lookups
// and receptor registry.

namespace Whizbang.Core.Component.Tests.ImmediateDetachedCounts;

/// <summary>Invocation counts per command id. Each test uses a fresh id.</summary>
public static class ImmediateDetachedProbe {
  private static readonly ConcurrentDictionary<Guid, int> _immediateDetached = new();
  private static readonly ConcurrentDictionary<Guid, int> _handled = new();

  private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _fired = new();
  private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _released = new();

  public static void RecordImmediateDetached(Guid id) {
    _immediateDetached.AddOrUpdate(id, 1, static (_, n) => n + 1);
    Fired(id).TrySetResult();
  }

  /// <summary>Completes when an ImmediateDetached receptor for <paramref name="id"/> has started (#1393).</summary>
  public static TaskCompletionSource Fired(Guid id) => _fired.GetOrAdd(id, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

  /// <summary>What a gated ImmediateDetached receptor waits on before it finishes (#1393).</summary>
  public static TaskCompletionSource Released(Guid id) => _released.GetOrAdd(id, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
  public static void RecordHandled(Guid id) => _handled.AddOrUpdate(id, 1, static (_, n) => n + 1);

  public static int ImmediateDetachedCount(Guid id) => _immediateDetached.TryGetValue(id, out var n) ? n : 0;
  public static int HandledCount(Guid id) => _handled.TryGetValue(id, out var n) ? n : 0;
}

public record CountedVoidCommand(Guid Id) : ICommand;

public record CountedResultCommand(Guid Id) : ICommand;

public record CountedResult(Guid Id);

public sealed class CountedVoidCommandReceptor : IReceptor<CountedVoidCommand> {
  public ValueTask HandleAsync(CountedVoidCommand message, CancellationToken cancellationToken = default) {
    ImmediateDetachedProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

public sealed class CountedResultCommandReceptor : IReceptor<CountedResultCommand, CountedResult> {
  public ValueTask<CountedResult> HandleAsync(CountedResultCommand message, CancellationToken cancellationToken = default) {
    ImmediateDetachedProbe.RecordHandled(message.Id);
    return ValueTask.FromResult(new CountedResult(message.Id));
  }
}

[FireAt(LifecycleStage.ImmediateDetached)]
[ReceptorIdempotent]
public sealed class CountedVoidCommandImmediateDetachedCounter : IReceptor<CountedVoidCommand> {
  public ValueTask HandleAsync(CountedVoidCommand message, CancellationToken cancellationToken = default) {
    ImmediateDetachedProbe.RecordImmediateDetached(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.ImmediateDetached)]
[ReceptorIdempotent]
public sealed class CountedResultCommandImmediateDetachedCounter : IReceptor<CountedResultCommand> {
  public ValueTask HandleAsync(CountedResultCommand message, CancellationToken cancellationToken = default) {
    ImmediateDetachedProbe.RecordImmediateDetached(message.Id);
    return ValueTask.CompletedTask;
  }
}

/// <summary>A command whose ImmediateDetached receptor does not finish until the test releases it (#1393).</summary>
public record GatedDetachedCommand(Guid Id) : ICommand;

/// <summary>A command whose ImmediateDetached receptor throws (#1393).</summary>
public record ThrowingDetachedCommand(Guid Id) : ICommand;

public sealed class GatedDetachedCommandReceptor : IReceptor<GatedDetachedCommand> {
  public ValueTask HandleAsync(GatedDetachedCommand message, CancellationToken cancellationToken = default) {
    ImmediateDetachedProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

public sealed class ThrowingDetachedCommandReceptor : IReceptor<ThrowingDetachedCommand> {
  public ValueTask HandleAsync(ThrowingDetachedCommand message, CancellationToken cancellationToken = default) {
    ImmediateDetachedProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.ImmediateDetached)]
public sealed class GatedDetachedCommandImmediateDetached : IReceptor<GatedDetachedCommand> {
  public async ValueTask HandleAsync(GatedDetachedCommand message, CancellationToken cancellationToken = default) {
    ImmediateDetachedProbe.RecordImmediateDetached(message.Id);
    await ImmediateDetachedProbe.Released(message.Id).Task;
  }
}

[FireAt(LifecycleStage.ImmediateDetached)]
public sealed class ThrowingDetachedCommandImmediateDetached : IReceptor<ThrowingDetachedCommand> {
  public ValueTask HandleAsync(ThrowingDetachedCommand message, CancellationToken cancellationToken = default) {
    ImmediateDetachedProbe.RecordImmediateDetached(message.Id);
    throw new InvalidOperationException("an ImmediateDetached receptor failed");
  }
}
