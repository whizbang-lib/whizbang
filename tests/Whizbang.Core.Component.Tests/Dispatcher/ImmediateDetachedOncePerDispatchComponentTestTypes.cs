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

  public static void RecordImmediateDetached(Guid id) => _immediateDetached.AddOrUpdate(id, 1, static (_, n) => n + 1);
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
