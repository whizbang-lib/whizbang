// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core;
using Whizbang.Core.Messaging;

// Types for DistributeStageOwnedCommandSendComponentTests (#1379). The trigger events belong to a
// namespace this service does not own, so publishing one queues an outbox row and a strategy flush
// fires the distribute stages for it. The command belongs to an owned namespace and is handled here
// by a receptor that returns no result, the shape SendAsync used to drop.

namespace Whizbang.Core.Component.Tests.DistributeStageSends {
  /// <summary>Completion signals, invocation count and send outcome per owned-command id.</summary>
  public static class DistributeStageSendProbe {
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _handled = new();
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource<DeliveryStatus>> _sendReturned = new();
    private static readonly ConcurrentDictionary<Guid, int> _counts = new();

    /// <summary>Completes when the owned command's receptor has run.</summary>
    public static TaskCompletionSource Handled(Guid id) =>
      _handled.GetOrAdd(id, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Completes with the receipt status when the hook's SendAsync call has returned.</summary>
    public static TaskCompletionSource<DeliveryStatus> SendReturned(Guid id) =>
      _sendReturned.GetOrAdd(id, static _ => new TaskCompletionSource<DeliveryStatus>(TaskCreationOptions.RunContinuationsAsynchronously));

    public static void RecordHandled(Guid id) {
      _counts.AddOrUpdate(id, 1, static (_, n) => n + 1);
      Handled(id).TrySetResult();
    }

    public static void RecordSendReturned(Guid id, DeliveryStatus status) => SendReturned(id).TrySetResult(status);

    /// <summary>A send that threw fails both waits with the cause instead of a timeout.</summary>
    public static void RecordSendFailed(Guid id, Exception exception) {
      Handled(id).TrySetException(exception);
      SendReturned(id).TrySetException(exception);
    }

    public static int CountFor(Guid id) => _counts.TryGetValue(id, out var n) ? n : 0;
  }

  public interface IDistributeStageTrigger {
    Guid Id { get; }
  }

  public static class DistributeStageTriggers {
    public static object For(LifecycleStage stage, Guid id) => stage switch {
      LifecycleStage.PreDistributeInline => new Upstream.PreDistributeInlineTrigger(id),
      LifecycleStage.PreDistributeDetached => new Upstream.PreDistributeDetachedTrigger(id),
      LifecycleStage.DistributeDetached => new Upstream.DistributeDetachedTrigger(id),
      LifecycleStage.PostDistributeInline => new Upstream.PostDistributeInlineTrigger(id),
      LifecycleStage.PostDistributeDetached => new Upstream.PostDistributeDetachedTrigger(id),
      _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "No distribute-stage trigger declared for this stage")
    };

    /// <summary>
    /// The send every stage hook makes. Detached stages run on a background task whose failures the
    /// framework only logs, so a throw is reported to the probe rather than lost.
    /// </summary>
    public static async ValueTask SendOwnedCommandAsync(IDispatcher dispatcher, Guid id) {
      try {
        var receipt = await dispatcher.SendAsync(new Owned.DistributeStageOwnedCommand(id));
        DistributeStageSendProbe.RecordSendReturned(id, receipt.Status);
      } catch (Exception ex) {
        DistributeStageSendProbe.RecordSendFailed(id, ex);
      }
    }
  }
}

namespace Whizbang.Core.Component.Tests.DistributeStageSends.Owned {
  public sealed record DistributeStageOwnedCommand(Guid Id) : ICommand;

  public sealed class DistributeStageOwnedCommandReceptor : IReceptor<DistributeStageOwnedCommand> {
    public ValueTask HandleAsync(DistributeStageOwnedCommand message, CancellationToken cancellationToken = default) {
      DistributeStageSendProbe.RecordHandled(message.Id);
      return ValueTask.CompletedTask;
    }
  }
}

namespace Whizbang.Core.Component.Tests.DistributeStageSends.Upstream {
  public sealed record PreDistributeInlineTrigger([property: StreamId] Guid Id) : IEvent, IDistributeStageTrigger;

  [FireAt(LifecycleStage.PreDistributeInline)]
  public sealed class PreDistributeInlineHook(IDispatcher dispatcher) : IReceptor<PreDistributeInlineTrigger> {
    public ValueTask HandleAsync(PreDistributeInlineTrigger message, CancellationToken cancellationToken = default) =>
      DistributeStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }

  public sealed record PreDistributeDetachedTrigger([property: StreamId] Guid Id) : IEvent, IDistributeStageTrigger;

  [FireAt(LifecycleStage.PreDistributeDetached)]
  public sealed class PreDistributeDetachedHook(IDispatcher dispatcher) : IReceptor<PreDistributeDetachedTrigger> {
    public ValueTask HandleAsync(PreDistributeDetachedTrigger message, CancellationToken cancellationToken = default) =>
      DistributeStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }

  public sealed record DistributeDetachedTrigger([property: StreamId] Guid Id) : IEvent, IDistributeStageTrigger;

  [FireAt(LifecycleStage.DistributeDetached)]
  public sealed class DistributeDetachedHook(IDispatcher dispatcher) : IReceptor<DistributeDetachedTrigger> {
    public ValueTask HandleAsync(DistributeDetachedTrigger message, CancellationToken cancellationToken = default) =>
      DistributeStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }

  public sealed record PostDistributeInlineTrigger([property: StreamId] Guid Id) : IEvent, IDistributeStageTrigger;

  [FireAt(LifecycleStage.PostDistributeInline)]
  public sealed class PostDistributeInlineHook(IDispatcher dispatcher) : IReceptor<PostDistributeInlineTrigger> {
    public ValueTask HandleAsync(PostDistributeInlineTrigger message, CancellationToken cancellationToken = default) =>
      DistributeStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }

  public sealed record PostDistributeDetachedTrigger([property: StreamId] Guid Id) : IEvent, IDistributeStageTrigger;

  [FireAt(LifecycleStage.PostDistributeDetached)]
  public sealed class PostDistributeDetachedHook(IDispatcher dispatcher) : IReceptor<PostDistributeDetachedTrigger> {
    public ValueTask HandleAsync(PostDistributeDetachedTrigger message, CancellationToken cancellationToken = default) =>
      DistributeStageTriggers.SendOwnedCommandAsync(dispatcher, message.Id);
  }
}
