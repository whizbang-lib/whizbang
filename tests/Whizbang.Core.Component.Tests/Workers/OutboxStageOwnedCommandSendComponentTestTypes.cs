// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core;
using Whizbang.Core.Messaging;

// Types for OutboxStageOwnedCommandSendComponentTests (#1379). Both the trigger events and the
// command live under one owned root namespace. The triggers must be owned EVENTS: once owned domains
// are configured, the receptor invoker skips the PreOutbox stages for owned commands and for events
// the service does not own, so only an owned event reaches a PreOutbox hook. The command is handled
// by a receptor that returns no result, the shape SendAsync used to drop.

namespace Whizbang.Core.Component.Tests.OutboxStageSends {
  /// <summary>Completion signal and invocation count per owned-command id.</summary>
  public static class OutboxStageSendProbe {
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _signals = new();
    private static readonly ConcurrentDictionary<Guid, int> _counts = new();

    public static TaskCompletionSource Expect(Guid id) =>
      _signals.GetOrAdd(id, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public static void Handled(Guid id) {
      _counts.AddOrUpdate(id, 1, static (_, n) => n + 1);
      Expect(id).TrySetResult();
    }

    /// <summary>A send that threw fails the waiting test with the cause instead of a timeout.</summary>
    public static void SendFailed(Guid id, Exception exception) => Expect(id).TrySetException(exception);

    public static int CountFor(Guid id) => _counts.TryGetValue(id, out var n) ? n : 0;
  }

  public interface IOutboxStageTrigger {
    Guid Id { get; }
  }

  public static class OutboxStageTriggers {
    public static Guid IdOf(object trigger) => ((IOutboxStageTrigger)trigger).Id;

    public static object For(LifecycleStage stage) {
      var id = Guid.CreateVersion7();
      return stage switch {
        LifecycleStage.PreOutboxInline => new Owned.Events.PreOutboxInlineTrigger(id),
        LifecycleStage.PreOutboxDetached => new Owned.Events.PreOutboxDetachedTrigger(id),
        LifecycleStage.PostOutboxInline => new Owned.Events.PostOutboxInlineTrigger(id),
        LifecycleStage.PostOutboxDetached => new Owned.Events.PostOutboxDetachedTrigger(id),
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "No trigger declared for this stage")
      };
    }

    /// <summary>The send every stage hook makes; a throw is reported rather than lost on a detached task.</summary>
    public static async ValueTask SendOwnedCommandAsync(IDispatcher dispatcher, Guid id) {
      try {
        await dispatcher.SendAsync(new Owned.Commands.OutboxStageOwnedCommand(id));
      } catch (Exception ex) {
        OutboxStageSendProbe.SendFailed(id, ex);
      }
    }
  }
}

namespace Whizbang.Core.Component.Tests.OutboxStageSends.Owned.Commands {
  public sealed record OutboxStageOwnedCommand(Guid Id) : ICommand;

  public sealed class OutboxStageOwnedCommandReceptor : IReceptor<OutboxStageOwnedCommand> {
    public ValueTask HandleAsync(OutboxStageOwnedCommand message, CancellationToken cancellationToken = default) {
      OutboxStageSendProbe.Handled(message.Id);
      return ValueTask.CompletedTask;
    }
  }
}

namespace Whizbang.Core.Component.Tests.OutboxStageSends.Owned.Events {
  public sealed record PreOutboxInlineTrigger([property: StreamId] Guid Id) : IEvent, IOutboxStageTrigger;

  [FireAt(LifecycleStage.PreOutboxInline)]
  public sealed class PreOutboxInlineHook(IDispatcher dispatcher) : IReceptor<PreOutboxInlineTrigger> {
    public ValueTask HandleAsync(PreOutboxInlineTrigger message, CancellationToken cancellationToken = default) =>
      OutboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }

  public sealed record PreOutboxDetachedTrigger([property: StreamId] Guid Id) : IEvent, IOutboxStageTrigger;

  [FireAt(LifecycleStage.PreOutboxDetached)]
  public sealed class PreOutboxDetachedHook(IDispatcher dispatcher) : IReceptor<PreOutboxDetachedTrigger> {
    public ValueTask HandleAsync(PreOutboxDetachedTrigger message, CancellationToken cancellationToken = default) =>
      OutboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }

  public sealed record PostOutboxInlineTrigger([property: StreamId] Guid Id) : IEvent, IOutboxStageTrigger;

  [FireAt(LifecycleStage.PostOutboxInline)]
  public sealed class PostOutboxInlineHook(IDispatcher dispatcher) : IReceptor<PostOutboxInlineTrigger> {
    public ValueTask HandleAsync(PostOutboxInlineTrigger message, CancellationToken cancellationToken = default) =>
      OutboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }

  public sealed record PostOutboxDetachedTrigger([property: StreamId] Guid Id) : IEvent, IOutboxStageTrigger;

  [FireAt(LifecycleStage.PostOutboxDetached)]
  public sealed class PostOutboxDetachedHook(IDispatcher dispatcher) : IReceptor<PostOutboxDetachedTrigger> {
    public ValueTask HandleAsync(PostOutboxDetachedTrigger message, CancellationToken cancellationToken = default) =>
      OutboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }
}
