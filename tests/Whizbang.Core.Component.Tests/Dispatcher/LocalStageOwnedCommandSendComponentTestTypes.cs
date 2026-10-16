// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

// Types for LocalStageOwnedCommandSendComponentTests (#1379). The trigger commands belong to a
// namespace the service does not own and have their own local handler; the command the stage hook
// sends belongs to an owned namespace and is handled by a receptor that returns no result, the
// shape SendAsync used to drop. The source generator discovers every receptor here, so the tests
// run the REAL generated lookup tables and receptor registry.

namespace Whizbang.Core.Component.Tests.LocalStageSends {
  /// <summary>
  /// Per owned-command id: a signal for the owned receptor completing, a signal for the hook's
  /// send returning, and the receptor's invocation count. Each test uses a fresh id.
  /// </summary>
  public static class LocalStageSendProbe {
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _handled = new();
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _sendReturned = new();
    private static readonly ConcurrentDictionary<Guid, int> _counts = new();

    public static TaskCompletionSource Handled(Guid id) =>
      _handled.GetOrAdd(id, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public static TaskCompletionSource SendReturned(Guid id) =>
      _sendReturned.GetOrAdd(id, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public static void RecordHandled(Guid id) {
      _counts.AddOrUpdate(id, 1, static (_, n) => n + 1);
      Handled(id).TrySetResult();
    }

    public static void RecordSendReturned(Guid id) => SendReturned(id).TrySetResult();

    /// <summary>A send that threw fails the waiting test with the cause instead of a timeout.</summary>
    public static void RecordSendFailed(Guid id, Exception exception) {
      Handled(id).TrySetException(exception);
      SendReturned(id).TrySetException(exception);
    }

    public static int CountFor(Guid id) => _counts.TryGetValue(id, out var n) ? n : 0;
  }

  public interface ILocalStageTrigger {
    Guid Id { get; }
  }

  public static class LocalStageTriggers {
    public static Guid IdOf(object trigger) => ((ILocalStageTrigger)trigger).Id;

    public static object For(LifecycleStage stage) {
      var id = Guid.CreateVersion7();
      return stage switch {
        LifecycleStage.ImmediateDetached => new Upstream.ImmediateDetachedTrigger(id),
        LifecycleStage.PostLifecycleInline => new Upstream.LocalPostLifecycleInlineTrigger(id),
        LifecycleStage.PostLifecycleDetached => new Upstream.LocalPostLifecycleDetachedTrigger(id),
        LifecycleStage.LocalImmediateInline => new Upstream.LocalImmediateInlineTrigger(id),
        LifecycleStage.LocalImmediateDetached => new Upstream.LocalImmediateDetachedTrigger(id),
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "No local trigger declared for this stage")
      };
    }

    /// <summary>The send every stage receptor makes; a throw is reported rather than lost on a detached task.</summary>
    public static async ValueTask SendOwnedCommandAsync(IDispatcher dispatcher, Guid id) {
      try {
        await dispatcher.SendAsync(new Owned.OwnedLocalStageCommand(id));
        LocalStageSendProbe.RecordSendReturned(id);
      } catch (Exception ex) {
        LocalStageSendProbe.RecordSendFailed(id, ex);
      }
    }
  }
}

namespace Whizbang.Core.Component.Tests.LocalStageSends.Owned {
  public sealed record OwnedLocalStageCommand(Guid Id) : ICommand;

  public sealed class OwnedLocalStageCommandReceptor : IReceptor<OwnedLocalStageCommand> {
    public ValueTask HandleAsync(OwnedLocalStageCommand message, CancellationToken cancellationToken = default) {
      LocalStageSendProbe.RecordHandled(message.Id);
      return ValueTask.CompletedTask;
    }
  }
}

namespace Whizbang.Core.Component.Tests.LocalStageSends.Upstream {
  // ImmediateDetached: fires after SendAsync's business receptor completes

  public sealed record ImmediateDetachedTrigger(Guid Id) : ICommand, ILocalStageTrigger;

  public sealed class ImmediateDetachedTriggerReceptor : IReceptor<ImmediateDetachedTrigger> {
    public ValueTask HandleAsync(ImmediateDetachedTrigger message, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  [FireAt(LifecycleStage.ImmediateDetached)]
  public sealed class ImmediateDetachedHook(IDispatcher dispatcher) : IReceptor<ImmediateDetachedTrigger> {
    public ValueTask HandleAsync(ImmediateDetachedTrigger message, CancellationToken cancellationToken = default) =>
      LocalStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }

  // PostLifecycle: fires after LocalInvokeAsync's business receptor completes

  public sealed record LocalPostLifecycleInlineTrigger(Guid Id) : ICommand, ILocalStageTrigger;

  public sealed class LocalPostLifecycleInlineTriggerReceptor : IReceptor<LocalPostLifecycleInlineTrigger> {
    public ValueTask HandleAsync(LocalPostLifecycleInlineTrigger message, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  [FireAt(LifecycleStage.PostLifecycleInline)]
  public sealed class LocalPostLifecycleInlineHook(IDispatcher dispatcher) : IReceptor<LocalPostLifecycleInlineTrigger> {
    public ValueTask HandleAsync(LocalPostLifecycleInlineTrigger message, CancellationToken cancellationToken = default) =>
      LocalStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }

  public sealed record LocalPostLifecycleDetachedTrigger(Guid Id) : ICommand, ILocalStageTrigger;

  public sealed class LocalPostLifecycleDetachedTriggerReceptor : IReceptor<LocalPostLifecycleDetachedTrigger> {
    public ValueTask HandleAsync(LocalPostLifecycleDetachedTrigger message, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  [FireAt(LifecycleStage.PostLifecycleDetached)]
  public sealed class LocalPostLifecycleDetachedHook(IDispatcher dispatcher) : IReceptor<LocalPostLifecycleDetachedTrigger> {
    public ValueTask HandleAsync(LocalPostLifecycleDetachedTrigger message, CancellationToken cancellationToken = default) =>
      LocalStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }

  // LocalImmediate: no production code fires these as lifecycle stages; a receptor declared at
  // one IS the message's handler for local dispatch, so the trigger's handler makes the send

  public sealed record LocalImmediateInlineTrigger(Guid Id) : ICommand, ILocalStageTrigger;

  [FireAt(LifecycleStage.LocalImmediateInline)]
  public sealed class LocalImmediateInlineTriggerReceptor(IDispatcher dispatcher) : IReceptor<LocalImmediateInlineTrigger> {
    public ValueTask HandleAsync(LocalImmediateInlineTrigger message, CancellationToken cancellationToken = default) =>
      LocalStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }

  public sealed record LocalImmediateDetachedTrigger(Guid Id) : ICommand, ILocalStageTrigger;

  [FireAt(LifecycleStage.LocalImmediateDetached)]
  public sealed class LocalImmediateDetachedTriggerReceptor(IDispatcher dispatcher) : IReceptor<LocalImmediateDetachedTrigger> {
    public ValueTask HandleAsync(LocalImmediateDetachedTrigger message, CancellationToken cancellationToken = default) =>
      LocalStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }
}
