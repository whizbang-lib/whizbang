// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;

// Types for RuntimeReceptorLocalDispatchComponentTests (#1385). The commands here have NO
// compile-time receptor: their only handler is registered into IReceptorRegistry at runtime, the
// way the framework's own registrars wire built-in receptors. The runtime receptor is generic, so
// the source generator does not discover it. The one compile-time receptor is for a framework
// system command, to show a generated handler of a broadcast command still runs on the sender.

namespace Whizbang.Core.Component.Tests.RuntimeLocalReceptors {
  /// <summary>
  /// Per probe id: a signal for the receptor completing and its invocation count. Each test uses a
  /// fresh id.
  /// </summary>
  public static class RuntimeReceptorProbe {
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _handled = new();
    private static readonly ConcurrentDictionary<Guid, int> _counts = new();

    public static TaskCompletionSource Handled(Guid id) =>
      _handled.GetOrAdd(id, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public static void RecordHandled(Guid id) {
      _counts.AddOrUpdate(id, 1, static (_, n) => n + 1);
      Handled(id).TrySetResult();
    }

    public static int CountFor(Guid id) => _counts.TryGetValue(id, out var n) ? n : 0;
  }

  public interface IRuntimeProbeMessage : IMessage {
    Guid Id { get; }
  }

  /// <summary>A command in a namespace the service does not own.</summary>
  public record UnownedRuntimeCommand(Guid Id) : ICommand, IRuntimeProbeMessage;

  /// <summary>A command answered by a runtime receptor that returns a result.</summary>
  public record RuntimeResultCommand(Guid Id) : ICommand, IRuntimeProbeMessage;

  public record RuntimeResult(Guid Id, string Answer);

  /// <summary>
  /// A runtime-registered receptor that returns nothing. Generic so the generator skips it: only a
  /// runtime registration makes it a handler.
  /// </summary>
  public sealed class RecordingRuntimeReceptor<TMessage> : IReceptor<TMessage> where TMessage : IRuntimeProbeMessage {
    public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
      RuntimeReceptorProbe.RecordHandled(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>A runtime-registered receptor that returns a result. Generic for the same reason.</summary>
  public sealed class AnsweringRuntimeReceptor<TMessage> : IReceptor<TMessage, RuntimeResult> where TMessage : IRuntimeProbeMessage {
    public ValueTask<RuntimeResult> HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
      RuntimeReceptorProbe.RecordHandled(message.Id);
      return ValueTask.FromResult(new RuntimeResult(message.Id, "answered at runtime"));
    }
  }

  /// <summary>A note returned beside the result; neither a command nor an event, so it does not cascade.</summary>
  public record RuntimeNote(Guid Id);

  /// <summary>A runtime-registered receptor that returns its result inside a tuple. Generic for the same reason.</summary>
  public sealed class TupleAnsweringRuntimeReceptor<TMessage> : IReceptor<TMessage, (RuntimeResult, RuntimeNote)> where TMessage : IRuntimeProbeMessage {
    public ValueTask<(RuntimeResult, RuntimeNote)> HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
      RuntimeReceptorProbe.RecordHandled(message.Id);
      return ValueTask.FromResult((new RuntimeResult(message.Id, "inside a tuple"), new RuntimeNote(message.Id)));
    }
  }

  /// <summary>
  /// A runtime receptor for a framework system command, keyed by the probe id the test puts in the
  /// cache key. Generic so the generator skips it.
  /// </summary>
  public sealed class SystemCommandRuntimeReceptor<TCommand> : IReceptor<TCommand> where TCommand : ClearCacheCommand {
    public ValueTask HandleAsync(TCommand message, CancellationToken cancellationToken = default) {
      RuntimeReceptorProbe.RecordHandled(Guid.Parse(message.CacheKey!));
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// A COMPILE-TIME receptor for a framework system command, keyed by the command's correlation id.
  /// </summary>
  /// <summary>A command answered by a generated void SYNC receptor, with an ImmediateDetached hook beside it.</summary>
  public record SyncVoidCommand(Guid Id) : ICommand, IRuntimeProbeMessage;

  public sealed class SyncVoidCommandReceptor : ISyncReceptor<SyncVoidCommand> {
    public void Handle(SyncVoidCommand message) => RuntimeReceptorProbe.RecordHandled(message.Id);
  }

  [FireAt(LifecycleStage.ImmediateDetached)]
  [ReceptorIdempotent]
  public sealed class SyncVoidCommandImmediateDetachedHook : IReceptor<SyncVoidCommand> {
    private static int _count;

    public static int Count => Volatile.Read(ref _count);

    public ValueTask HandleAsync(SyncVoidCommand message, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _count);
      return ValueTask.CompletedTask;
    }
  }

  public sealed class DiagnosticsCommandProbeReceptor : IReceptor<DiagnosticsCommand> {
    public ValueTask HandleAsync(DiagnosticsCommand message, CancellationToken cancellationToken = default) {
      RuntimeReceptorProbe.RecordHandled(message.CorrelationId!.Value);
      return ValueTask.CompletedTask;
    }
  }
}

namespace Whizbang.Core.Component.Tests.RuntimeLocalReceptors.Owned {
  /// <summary>A command in a namespace the service owns, with only a runtime receptor.</summary>
  public record OwnedRuntimeCommand(Guid Id) : ICommand, IRuntimeProbeMessage;

  /// <summary>An owned command with no receptor of any kind.</summary>
  public record OwnedUnhandledCommand(Guid Id) : ICommand;
}
