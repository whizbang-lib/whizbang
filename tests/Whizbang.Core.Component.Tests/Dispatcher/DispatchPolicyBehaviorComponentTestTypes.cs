// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;

// Types for DispatchPolicyBehaviorComponentTests (#1395). The source generator discovers every
// non-generic receptor here, so the tests run the REAL generated dispatcher; the one generic receptor
// is registered at runtime only.

namespace Whizbang.Core.Component.Tests.PolicyBehavior {
  /// <summary>Invocation counts per message id; each test uses a fresh id.</summary>
  public static class PolicyProbe {
    private static readonly ConcurrentDictionary<(string, Guid), int> _counts = new();

    public static void Record(string receptor, Guid id) => _counts.AddOrUpdate((receptor, id), 1, static (_, n) => n + 1);

    public static int Count(string receptor, Guid id) => _counts.TryGetValue((receptor, id), out var n) ? n : 0;
  }

  public interface IPolicyProbeMessage : IMessage {
    Guid Id { get; }
  }

  public record PbUnownedCommand(Guid Id) : ICommand, IPolicyProbeMessage;

  public record PbThrowingUnownedCommand(Guid Id) : ICommand, IPolicyProbeMessage;

  public record PbEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

  public record PbHookedEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

  public record PbVoidOnlyEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

  public record PbRuntimeEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

  public record PbThrowingEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

  public sealed class PbThrowingEventReceptor : IReceptor<PbThrowingEvent> {
    public ValueTask HandleAsync(PbThrowingEvent message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(PbThrowingEventReceptor), message.Id);
      throw new InvalidOperationException("pb-event-boom");
    }
  }

  /// <summary>Cascades an event whose local receptor throws.</summary>
  public record PbThrowingCascadeTrigger(Guid Id) : ICommand, IPolicyProbeMessage;

  public sealed class PbThrowingCascadeTriggerReceptor : IReceptor<PbThrowingCascadeTrigger, PbThrowingEvent> {
    public ValueTask<PbThrowingEvent> HandleAsync(PbThrowingCascadeTrigger message, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(new PbThrowingEvent(message.Id));
  }

  /// <summary>A command nothing handles.</summary>
  public record PbUnhandledCommand(Guid Id) : ICommand, IPolicyProbeMessage;

  public record PbUnhandledResult(Guid Id);

  public record PbSyncCommand(Guid Id) : ICommand, IPolicyProbeMessage;

  public sealed class PbSyncCommandReceptor : ISyncReceptor<PbSyncCommand> {
    public void Handle(PbSyncCommand message) => PolicyProbe.Record(nameof(PbSyncCommandReceptor), message.Id);
  }

  public record PbSyncCommandWithLifecycle(Guid Id) : ICommand, IPolicyProbeMessage;

  public sealed class PbSyncCommandWithLifecycleReceptor : ISyncReceptor<PbSyncCommandWithLifecycle> {
    public void Handle(PbSyncCommandWithLifecycle message) => PolicyProbe.Record(nameof(PbSyncCommandWithLifecycleReceptor), message.Id);
  }

  [FireAt(LifecycleStage.PostLifecycleInline)]
  public sealed class PbSyncCommandWithLifecycleHook : IReceptor<PbSyncCommandWithLifecycle> {
    public ValueTask HandleAsync(PbSyncCommandWithLifecycle message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(PbSyncCommandWithLifecycleHook), message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record PbRpcRequest(Guid Id) : ICommand, IPolicyProbeMessage;

  public record PbRpcResponse(Guid Id);

  public record PbStoreOnlyTrigger(Guid Id) : ICommand, IPolicyProbeMessage;

  public sealed class PbUnownedCommandReceptor : IReceptor<PbUnownedCommand> {
    public ValueTask HandleAsync(PbUnownedCommand message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(PbUnownedCommandReceptor), message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public sealed class PbThrowingUnownedCommandReceptor : IReceptor<PbThrowingUnownedCommand> {
    public ValueTask HandleAsync(PbThrowingUnownedCommand message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(PbThrowingUnownedCommandReceptor), message.Id);
      throw new InvalidOperationException("pb-boom");
    }
  }

  public sealed class PbEventReceptor : IReceptor<PbEvent> {
    public ValueTask HandleAsync(PbEvent message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(PbEventReceptor), message.Id);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>A receptor declared at a local stage: it runs whenever this service handles the event (#1400).</summary>
  [FireAt(LifecycleStage.LocalImmediateInline)]
  public sealed class PbLocalStageHook : IReceptor<PbHookedEvent> {
    public ValueTask HandleAsync(PbHookedEvent message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(PbLocalStageHook), message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public sealed class PbVoidOnlyEventReceptor : IReceptor<PbVoidOnlyEvent> {
    public ValueTask HandleAsync(PbVoidOnlyEvent message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(PbVoidOnlyEventReceptor), message.Id);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Generic so the generator skips it; registered at runtime at a local stage (#1399).</summary>
  public sealed class PbRuntimeReceptor<TMessage> : IReceptor<TMessage> where TMessage : IPolicyProbeMessage {
    public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record("PbRuntimeReceptor", message.Id);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Answers with a response and cascades an event extracted from the result (#1404).</summary>
  public sealed class PbRpcRequestReceptor : IReceptor<PbRpcRequest, (PbRpcResponse, PbEvent)> {
    public ValueTask<(PbRpcResponse, PbEvent)> HandleAsync(PbRpcRequest message, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult((new PbRpcResponse(message.Id), new PbEvent(message.Id)));
  }

  public record PbStoreOnlyAck(Guid Id);

  /// <summary>
  /// Cascades a command routed to the event store only, which the policy refuses (M). The routed command
  /// rides in a tuple: a receptor's top-level Routed result is unwrapped by the generated invokers before
  /// the cascade sees its route (tracked separately).
  /// </summary>
  public sealed class PbStoreOnlyTriggerReceptor : IReceptor<PbStoreOnlyTrigger, (PbStoreOnlyAck, Routed<PbUnownedCommand>)> {
    public ValueTask<(PbStoreOnlyAck, Routed<PbUnownedCommand>)> HandleAsync(PbStoreOnlyTrigger message, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult((new PbStoreOnlyAck(message.Id), Route.EventStoreOnly(new PbUnownedCommand(message.Id))));
  }
}

namespace Whizbang.Core.Component.Tests.PolicyBehavior.Owned {
  public record PbOwnedCommand(Guid Id) : ICommand, IPolicyProbeMessage;

  /// <summary>An owned command this service does not handle.</summary>
  public record PbOwnedUnhandledCommand(Guid Id) : ICommand, IPolicyProbeMessage;

  public record PbCascadeTrigger(Guid Id) : ICommand, IPolicyProbeMessage;

  public sealed class PbOwnedCommandReceptor : IReceptor<PbOwnedCommand> {
    public ValueTask HandleAsync(PbOwnedCommand message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(PbOwnedCommandReceptor), message.Id);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Cascades an owned command with the default route (#1403).</summary>
  public sealed class PbCascadeTriggerReceptor : IReceptor<PbCascadeTrigger, PbOwnedCommand> {
    public ValueTask<PbOwnedCommand> HandleAsync(PbCascadeTrigger message, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(new PbOwnedCommand(message.Id));
  }

  /// <summary>An owned command whose handler returns events, as a tuple (#1410).</summary>
  public record PbOwnedTypedCommand(Guid Id) : ICommand, IPolicyProbeMessage;

  public record PbOwnedTypedFirstEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

  public record PbOwnedTypedSecondEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

  public sealed class PbOwnedTypedCommandReceptor : IReceptor<PbOwnedTypedCommand, (PbOwnedTypedFirstEvent, PbOwnedTypedSecondEvent)> {
    public ValueTask<(PbOwnedTypedFirstEvent, PbOwnedTypedSecondEvent)> HandleAsync(PbOwnedTypedCommand message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(PbOwnedTypedCommandReceptor), message.Id);
      return ValueTask.FromResult((new PbOwnedTypedFirstEvent(message.Id), new PbOwnedTypedSecondEvent(message.Id)));
    }
  }

  /// <summary>An owned command whose synchronous handler returns an event (#1410).</summary>
  public record PbOwnedSyncTypedCommand(Guid Id) : ICommand, IPolicyProbeMessage;

  public sealed class PbOwnedSyncTypedCommandReceptor : ISyncReceptor<PbOwnedSyncTypedCommand, PbOwnedTypedFirstEvent> {
    public PbOwnedTypedFirstEvent Handle(PbOwnedSyncTypedCommand message) {
      PolicyProbe.Record(nameof(PbOwnedSyncTypedCommandReceptor), message.Id);
      return new PbOwnedTypedFirstEvent(message.Id);
    }
  }

  /// <summary>
  /// An owned command whose handler declares <see cref="object"/> as its response: the one response type
  /// the generated publisher's own lookup resolves, so it must still run once, not twice (#1410).
  /// </summary>
  public record PbOwnedObjectResultCommand(Guid Id) : ICommand, IPolicyProbeMessage;

  public sealed class PbOwnedObjectResultCommandReceptor : IReceptor<PbOwnedObjectResultCommand, object> {
    public ValueTask<object> HandleAsync(PbOwnedObjectResultCommand message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(PbOwnedObjectResultCommandReceptor), message.Id);
      return ValueTask.FromResult<object>(new PbOwnedTypedFirstEvent(message.Id));
    }
  }

  /// <summary>Returns an owned command whose handler returns events, so the dispatcher cascades it (#1410).</summary>
  public record PbTypedCascadeTrigger(Guid Id) : ICommand, IPolicyProbeMessage;

  public sealed class PbTypedCascadeTriggerReceptor : IReceptor<PbTypedCascadeTrigger, PbOwnedTypedCommand> {
    public ValueTask<PbOwnedTypedCommand> HandleAsync(PbTypedCascadeTrigger message, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(new PbOwnedTypedCommand(message.Id));
  }
}
