// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;

// Types for LifecycleStageOwnedCommandSendIntegrationTests (#1379). The trigger events belong to
// another service's namespace; the command belongs to an owned namespace and is handled here by a
// receptor that returns no result, the shape SendAsync used to drop.

namespace Whizbang.Data.EFCore.Postgres.Tests.StageSends {
  /// <summary>Completion signal and invocation count per owned-command id.</summary>
  public static class StageSendProbe {
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

  public interface IStageTrigger {
    Guid Id { get; }
  }

  public static class StageTriggers {
    public static Guid IdOf(object trigger) => ((IStageTrigger)trigger).Id;

    public static object For(LifecycleStage stage, bool asSystemForTenant) {
      var id = Guid.CreateVersion7();
      return (stage, asSystemForTenant) switch {
        (LifecycleStage.PreInboxInline, false) => new Upstream.PreInboxInlineTrigger(id),
        (LifecycleStage.PreInboxDetached, false) => new Upstream.PreInboxDetachedTrigger(id),
        (LifecycleStage.PostInboxInline, false) => new Upstream.PostInboxInlineTrigger(id),
        (LifecycleStage.PostInboxDetached, false) => new Upstream.PostInboxDetachedTrigger(id),
        (LifecycleStage.PostAllPerspectivesInline, false) => new Upstream.PostAllPerspectivesInlineTrigger(id),
        (LifecycleStage.PostAllPerspectivesDetached, false) => new Upstream.PostAllPerspectivesDetachedTrigger(id),
        (LifecycleStage.PostLifecycleInline, false) => new Upstream.PostLifecycleInlineTrigger(id),
        (LifecycleStage.PostLifecycleDetached, false) => new Upstream.PostLifecycleDetachedTrigger(id),
        (LifecycleStage.PostInboxDetached, true) => new Upstream.PostInboxDetachedAsSystemTrigger(id),
        (LifecycleStage.PostAllPerspectivesDetached, true) => new Upstream.PostAllPerspectivesDetachedAsSystemTrigger(id),
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "No trigger declared for this stage")
      };
    }

    /// <summary>The send every stage hook makes; a throw is reported rather than lost on a detached task.</summary>
    public static async ValueTask SendOwnedCommandAsync(IDispatcher dispatcher, Guid id, bool asSystemForTenant) {
      try {
        var command = new Owned.OwnedStageCommand(id);
        if (asSystemForTenant) {
          await dispatcher.AsSystem().ForTenant("tenant-stage-sends").SendAsync(command);
        } else {
          await dispatcher.SendAsync(command);
        }
      } catch (Exception ex) {
        StageSendProbe.SendFailed(id, ex);
      }
    }
  }
}

namespace Whizbang.Data.EFCore.Postgres.Tests.StageSends.Owned {
  public sealed record OwnedStageCommand(Guid Id) : ICommand;

  public sealed class OwnedStageCommandReceptor : IReceptor<OwnedStageCommand> {
    public ValueTask HandleAsync(OwnedStageCommand message, CancellationToken cancellationToken = default) {
      StageSendProbe.Handled(message.Id);
      return ValueTask.CompletedTask;
    }
  }
}

namespace Whizbang.Data.EFCore.Postgres.Tests.StageSends.Upstream {
  public sealed record PreInboxInlineTrigger([property: StreamId] Guid Id) : IEvent, IStageTrigger;

  [FireAt(LifecycleStage.PreInboxInline)]
  public sealed class PreInboxInlineHook(IDispatcher dispatcher) : IReceptor<PreInboxInlineTrigger> {
    public ValueTask HandleAsync(PreInboxInlineTrigger message, CancellationToken cancellationToken = default) =>
      StageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PreInboxDetachedTrigger([property: StreamId] Guid Id) : IEvent, IStageTrigger;

  [FireAt(LifecycleStage.PreInboxDetached)]
  public sealed class PreInboxDetachedHook(IDispatcher dispatcher) : IReceptor<PreInboxDetachedTrigger> {
    public ValueTask HandleAsync(PreInboxDetachedTrigger message, CancellationToken cancellationToken = default) =>
      StageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostInboxInlineTrigger([property: StreamId] Guid Id) : IEvent, IStageTrigger;

  [FireAt(LifecycleStage.PostInboxInline)]
  public sealed class PostInboxInlineHook(IDispatcher dispatcher) : IReceptor<PostInboxInlineTrigger> {
    public ValueTask HandleAsync(PostInboxInlineTrigger message, CancellationToken cancellationToken = default) =>
      StageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostInboxDetachedTrigger([property: StreamId] Guid Id) : IEvent, IStageTrigger;

  [FireAt(LifecycleStage.PostInboxDetached)]
  public sealed class PostInboxDetachedHook(IDispatcher dispatcher) : IReceptor<PostInboxDetachedTrigger> {
    public ValueTask HandleAsync(PostInboxDetachedTrigger message, CancellationToken cancellationToken = default) =>
      StageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostAllPerspectivesInlineTrigger([property: StreamId] Guid Id) : IEvent, IStageTrigger;

  [FireAt(LifecycleStage.PostAllPerspectivesInline)]
  public sealed class PostAllPerspectivesInlineHook(IDispatcher dispatcher) : IReceptor<PostAllPerspectivesInlineTrigger> {
    public ValueTask HandleAsync(PostAllPerspectivesInlineTrigger message, CancellationToken cancellationToken = default) =>
      StageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostAllPerspectivesDetachedTrigger([property: StreamId] Guid Id) : IEvent, IStageTrigger;

  [FireAt(LifecycleStage.PostAllPerspectivesDetached)]
  public sealed class PostAllPerspectivesDetachedHook(IDispatcher dispatcher) : IReceptor<PostAllPerspectivesDetachedTrigger> {
    public ValueTask HandleAsync(PostAllPerspectivesDetachedTrigger message, CancellationToken cancellationToken = default) =>
      StageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostLifecycleInlineTrigger([property: StreamId] Guid Id) : IEvent, IStageTrigger;

  [FireAt(LifecycleStage.PostLifecycleInline)]
  public sealed class PostLifecycleInlineHook(IDispatcher dispatcher) : IReceptor<PostLifecycleInlineTrigger> {
    public ValueTask HandleAsync(PostLifecycleInlineTrigger message, CancellationToken cancellationToken = default) =>
      StageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostLifecycleDetachedTrigger([property: StreamId] Guid Id) : IEvent, IStageTrigger;

  [FireAt(LifecycleStage.PostLifecycleDetached)]
  public sealed class PostLifecycleDetachedHook(IDispatcher dispatcher) : IReceptor<PostLifecycleDetachedTrigger> {
    public ValueTask HandleAsync(PostLifecycleDetachedTrigger message, CancellationToken cancellationToken = default) =>
      StageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostInboxDetachedAsSystemTrigger([property: StreamId] Guid Id) : IEvent, IStageTrigger;

  [FireAt(LifecycleStage.PostInboxDetached)]
  public sealed class PostInboxDetachedAsSystemHook(IDispatcher dispatcher) : IReceptor<PostInboxDetachedAsSystemTrigger> {
    public ValueTask HandleAsync(PostInboxDetachedAsSystemTrigger message, CancellationToken cancellationToken = default) =>
      StageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: true);
  }

  public sealed record PostAllPerspectivesDetachedAsSystemTrigger([property: StreamId] Guid Id) : IEvent, IStageTrigger;

  [FireAt(LifecycleStage.PostAllPerspectivesDetached)]
  public sealed class PostAllPerspectivesDetachedAsSystemHook(IDispatcher dispatcher) : IReceptor<PostAllPerspectivesDetachedAsSystemTrigger> {
    public ValueTask HandleAsync(PostAllPerspectivesDetachedAsSystemTrigger message, CancellationToken cancellationToken = default) =>
      StageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: true);
  }

}
