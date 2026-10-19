// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

// Types for LocalImmediateDetachedHookComponentTests (#1392). Each command has a handler (no [FireAt], so the default
// LocalImmediateInline stage) and a [FireAt(LocalImmediateDetached)] hook. The source generator discovers every
// receptor here, so the tests run the REAL generated lookups and receptor registry.

namespace Whizbang.Core.Component.Tests.LocalDetachedHooks;

/// <summary>What ran, per command id, with a signal per hook. Each test uses a fresh id.</summary>
public static class LocalDetachedProbe {
  private static readonly ConcurrentDictionary<Guid, int> _handled = new();
  private static readonly ConcurrentDictionary<Guid, int> _hooked = new();
  private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _hookStarted = new();
  private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _released = new();

  public static void RecordHandled(Guid id) => _handled.AddOrUpdate(id, 1, static (_, n) => n + 1);

  public static void RecordHooked(Guid id) {
    _hooked.AddOrUpdate(id, 1, static (_, n) => n + 1);
    HookStarted(id).TrySetResult();
  }

  public static int HandledCount(Guid id) => _handled.TryGetValue(id, out var n) ? n : 0;
  public static int HookedCount(Guid id) => _hooked.TryGetValue(id, out var n) ? n : 0;

  public static TaskCompletionSource HookStarted(Guid id) => _hookStarted.GetOrAdd(id, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
  public static TaskCompletionSource Released(Guid id) => _released.GetOrAdd(id, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>A command whose LocalImmediateDetached hook does not finish until the test releases it.</summary>
public record LdGatedCommand(Guid Id) : ICommand;

/// <summary>A command whose LocalImmediateDetached hook throws.</summary>
public record LdThrowingCommand(Guid Id) : ICommand;

/// <summary>A command whose handler returns a result, beside a LocalImmediateDetached hook.</summary>
public record LdResultCommand(Guid Id) : ICommand;

public record LdResult(Guid Id);

public sealed class LdGatedCommandHandler : IReceptor<LdGatedCommand> {
  public ValueTask HandleAsync(LdGatedCommand message, CancellationToken cancellationToken = default) {
    LocalDetachedProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

public sealed class LdThrowingCommandHandler : IReceptor<LdThrowingCommand> {
  public ValueTask HandleAsync(LdThrowingCommand message, CancellationToken cancellationToken = default) {
    LocalDetachedProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

public sealed class LdResultCommandHandler : IReceptor<LdResultCommand, LdResult> {
  public ValueTask<LdResult> HandleAsync(LdResultCommand message, CancellationToken cancellationToken = default) {
    LocalDetachedProbe.RecordHandled(message.Id);
    return ValueTask.FromResult(new LdResult(message.Id));
  }
}

[FireAt(LifecycleStage.LocalImmediateDetached)]
public sealed class LdGatedCommandHook : IReceptor<LdGatedCommand> {
  public async ValueTask HandleAsync(LdGatedCommand message, CancellationToken cancellationToken = default) {
    LocalDetachedProbe.RecordHooked(message.Id);
    await LocalDetachedProbe.Released(message.Id).Task;
  }
}

[FireAt(LifecycleStage.LocalImmediateDetached)]
public sealed class LdThrowingCommandHook : IReceptor<LdThrowingCommand> {
  public ValueTask HandleAsync(LdThrowingCommand message, CancellationToken cancellationToken = default) {
    LocalDetachedProbe.RecordHooked(message.Id);
    throw new InvalidOperationException("a LocalImmediateDetached hook failed");
  }
}

[FireAt(LifecycleStage.LocalImmediateDetached)]
public sealed class LdResultCommandHook : IReceptor<LdResultCommand> {
  public ValueTask HandleAsync(LdResultCommand message, CancellationToken cancellationToken = default) {
    LocalDetachedProbe.RecordHooked(message.Id);
    return ValueTask.CompletedTask;
  }
}
