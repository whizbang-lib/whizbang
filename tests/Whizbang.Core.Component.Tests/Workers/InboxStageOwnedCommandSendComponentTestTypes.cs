// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;

// Types for InboxStageOwnedCommandSendComponentTests (#1379). The trigger events belong to another
// service's namespace; the command belongs to an owned namespace and is handled here by a receptor
// that returns no result, the shape SendAsync used to drop. The source generator discovers every
// receptor here, so the worker runs the REAL generated registry and dispatcher.

namespace Whizbang.Core.Component.Tests.InboxStageSends {
  /// <summary>
  /// Per owned-command id: a signal for the owned receptor completing, a signal for the hook's
  /// send returning, and the receptor's invocation count. Each test uses a fresh id.
  /// </summary>
  public static class InboxStageSendProbe {
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

  public interface IInboxStageTrigger {
    Guid Id { get; }
  }

  public static class InboxStageTriggers {
    public const string TENANT = "tenant-inbox-stage-sends";

    public static Guid IdOf(object trigger) => ((IInboxStageTrigger)trigger).Id;

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
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "No inbox trigger declared for this stage")
      };
    }

    /// <summary>The send every stage hook makes; a throw is reported rather than lost on a detached task.</summary>
    public static async ValueTask SendOwnedCommandAsync(IDispatcher dispatcher, Guid id, bool asSystemForTenant) {
      try {
        var command = new Owned.OwnedInboxStageCommand(id);
        if (asSystemForTenant) {
          await dispatcher.AsSystem().ForTenant(TENANT).SendAsync(command);
        } else {
          await dispatcher.SendAsync(command);
        }
        InboxStageSendProbe.RecordSendReturned(id);
      } catch (Exception ex) {
        InboxStageSendProbe.RecordSendFailed(id, ex);
      }
    }
  }
}

namespace Whizbang.Core.Component.Tests.InboxStageSends.Owned {
  public sealed record OwnedInboxStageCommand(Guid Id) : ICommand;

  public sealed class OwnedInboxStageCommandReceptor : IReceptor<OwnedInboxStageCommand> {
    public ValueTask HandleAsync(OwnedInboxStageCommand message, CancellationToken cancellationToken = default) {
      InboxStageSendProbe.RecordHandled(message.Id);
      return ValueTask.CompletedTask;
    }
  }
}

namespace Whizbang.Core.Component.Tests.InboxStageSends.Upstream {
  public sealed record PreInboxInlineTrigger([property: StreamId] Guid Id) : IEvent, IInboxStageTrigger;

  [FireAt(LifecycleStage.PreInboxInline)]
  public sealed class PreInboxInlineHook(IDispatcher dispatcher) : IReceptor<PreInboxInlineTrigger> {
    public ValueTask HandleAsync(PreInboxInlineTrigger message, CancellationToken cancellationToken = default) =>
      InboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PreInboxDetachedTrigger([property: StreamId] Guid Id) : IEvent, IInboxStageTrigger;

  [FireAt(LifecycleStage.PreInboxDetached)]
  public sealed class PreInboxDetachedHook(IDispatcher dispatcher) : IReceptor<PreInboxDetachedTrigger> {
    public ValueTask HandleAsync(PreInboxDetachedTrigger message, CancellationToken cancellationToken = default) =>
      InboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostInboxInlineTrigger([property: StreamId] Guid Id) : IEvent, IInboxStageTrigger;

  [FireAt(LifecycleStage.PostInboxInline)]
  public sealed class PostInboxInlineHook(IDispatcher dispatcher) : IReceptor<PostInboxInlineTrigger> {
    public ValueTask HandleAsync(PostInboxInlineTrigger message, CancellationToken cancellationToken = default) =>
      InboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostInboxDetachedTrigger([property: StreamId] Guid Id) : IEvent, IInboxStageTrigger;

  [FireAt(LifecycleStage.PostInboxDetached)]
  public sealed class PostInboxDetachedHook(IDispatcher dispatcher) : IReceptor<PostInboxDetachedTrigger> {
    public ValueTask HandleAsync(PostInboxDetachedTrigger message, CancellationToken cancellationToken = default) =>
      InboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostAllPerspectivesInlineTrigger([property: StreamId] Guid Id) : IEvent, IInboxStageTrigger;

  [FireAt(LifecycleStage.PostAllPerspectivesInline)]
  public sealed class PostAllPerspectivesInlineHook(IDispatcher dispatcher) : IReceptor<PostAllPerspectivesInlineTrigger> {
    public ValueTask HandleAsync(PostAllPerspectivesInlineTrigger message, CancellationToken cancellationToken = default) =>
      InboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostAllPerspectivesDetachedTrigger([property: StreamId] Guid Id) : IEvent, IInboxStageTrigger;

  [FireAt(LifecycleStage.PostAllPerspectivesDetached)]
  public sealed class PostAllPerspectivesDetachedHook(IDispatcher dispatcher) : IReceptor<PostAllPerspectivesDetachedTrigger> {
    public ValueTask HandleAsync(PostAllPerspectivesDetachedTrigger message, CancellationToken cancellationToken = default) =>
      InboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostLifecycleInlineTrigger([property: StreamId] Guid Id) : IEvent, IInboxStageTrigger;

  [FireAt(LifecycleStage.PostLifecycleInline)]
  public sealed class PostLifecycleInlineHook(IDispatcher dispatcher) : IReceptor<PostLifecycleInlineTrigger> {
    public ValueTask HandleAsync(PostLifecycleInlineTrigger message, CancellationToken cancellationToken = default) =>
      InboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostLifecycleDetachedTrigger([property: StreamId] Guid Id) : IEvent, IInboxStageTrigger;

  [FireAt(LifecycleStage.PostLifecycleDetached)]
  public sealed class PostLifecycleDetachedHook(IDispatcher dispatcher) : IReceptor<PostLifecycleDetachedTrigger> {
    public ValueTask HandleAsync(PostLifecycleDetachedTrigger message, CancellationToken cancellationToken = default) =>
      InboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: false);
  }

  public sealed record PostInboxDetachedAsSystemTrigger([property: StreamId] Guid Id) : IEvent, IInboxStageTrigger;

  [FireAt(LifecycleStage.PostInboxDetached)]
  public sealed class PostInboxDetachedAsSystemHook(IDispatcher dispatcher) : IReceptor<PostInboxDetachedAsSystemTrigger> {
    public ValueTask HandleAsync(PostInboxDetachedAsSystemTrigger message, CancellationToken cancellationToken = default) =>
      InboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: true);
  }

  public sealed record PostAllPerspectivesDetachedAsSystemTrigger([property: StreamId] Guid Id) : IEvent, IInboxStageTrigger;

  [FireAt(LifecycleStage.PostAllPerspectivesDetached)]
  public sealed class PostAllPerspectivesDetachedAsSystemHook(IDispatcher dispatcher) : IReceptor<PostAllPerspectivesDetachedAsSystemTrigger> {
    public ValueTask HandleAsync(PostAllPerspectivesDetachedAsSystemTrigger message, CancellationToken cancellationToken = default) =>
      InboxStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id, asSystemForTenant: true);
  }
}
