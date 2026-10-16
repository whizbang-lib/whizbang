// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core;
using Whizbang.Core.Dispatch;

// Types for PerspectiveStageOwnedCommandSendComponentTests (#1379). The trigger event lives in a
// namespace the service does not own, the way another service's event does. The stage hook is generic
// so the source generator does not discover it: each test registers it at runtime, at one stage, on its
// own receptor registry. The command belongs to an owned namespace and is handled by a receptor that
// returns no result, the shape SendAsync used to drop.

namespace Whizbang.Core.Component.Tests.PerspectiveStageSends {
  /// <summary>Completion signal and invocation count per owned-command id.</summary>
  public static class PerspectiveStageSendProbe {
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _signals = new();
    private static readonly ConcurrentDictionary<Guid, int> _counts = new();

    public static TaskCompletionSource Expect(Guid id) =>
      _signals.GetOrAdd(id, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public static void Handled(Guid id) {
      _counts.AddOrUpdate(id, 1, static (_, n) => n + 1);
      Expect(id).TrySetResult();
    }

    /// <summary>A send that threw, or a perspective failure, fails the waiting test with the cause instead of a timeout.</summary>
    public static void Failed(Guid id, Exception exception) => Expect(id).TrySetException(exception);

    public static int CountFor(Guid id) => _counts.TryGetValue(id, out var n) ? n : 0;
  }

  /// <summary>
  /// A lifecycle receptor registered at runtime at one perspective-path stage. For its trigger it
  /// sends the owned command exactly the way an application receptor would, through the dispatcher.
  /// A throw is reported to the probe rather than lost on a detached task.
  /// </summary>
  /// <typeparam name="TEvent">The trigger event type.</typeparam>
  public sealed class PerspectiveStageSendHook<TEvent>(
      IDispatcher dispatcher,
      Func<TEvent, bool> isTrigger,
      Guid commandId,
      bool asSystemForTenant) : IReceptor<TEvent> where TEvent : IEvent {
    private int _invocations;

    /// <summary>How many times the hook ran for its trigger.</summary>
    public int Invocations => Volatile.Read(ref _invocations);

    public async ValueTask HandleAsync(TEvent message, CancellationToken cancellationToken = default) {
      if (!isTrigger(message)) {
        return;
      }
      Interlocked.Increment(ref _invocations);
      try {
        var command = new Owned.PerspectiveStageOwnedCommand(commandId);
        if (asSystemForTenant) {
          await dispatcher.AsSystem().ForTenant("tenant-perspective-stage-sends").SendAsync(command);
        } else {
          await dispatcher.SendAsync(command);
        }
      } catch (Exception ex) {
        PerspectiveStageSendProbe.Failed(commandId, ex);
      }
    }
  }
}

namespace Whizbang.Core.Component.Tests.PerspectiveStageSends.Upstream {
  /// <summary>Another service's event, with a perspective in this one.</summary>
  public sealed record PerspectiveStageTrigger([property: StreamId] Guid OrderId) : IEvent;
}

namespace Whizbang.Core.Component.Tests.PerspectiveStageSends.Owned {
  public sealed record PerspectiveStageOwnedCommand(Guid Id) : ICommand;

  public sealed class PerspectiveStageOwnedCommandReceptor : IReceptor<PerspectiveStageOwnedCommand> {
    public ValueTask HandleAsync(PerspectiveStageOwnedCommand message, CancellationToken cancellationToken = default) {
      PerspectiveStageSendProbe.Handled(message.Id);
      return ValueTask.CompletedTask;
    }
  }
}
