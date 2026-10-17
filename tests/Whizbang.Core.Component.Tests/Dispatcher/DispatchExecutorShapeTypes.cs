// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Component.Tests.PolicyBehavior;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;

// Handler shapes for DispatchExecutorShapeComponentTests. The source generator discovers every
// receptor here, so the tests run the REAL generated dispatcher and registry. Every message lives in
// the owned namespace (the tests configure it as owned), so nothing reaches the outbox unless it is
// meant to, and each receptor records into PolicyProbe under its own name.

namespace Whizbang.Core.Component.Tests.ExecutorShapes.Owned;

/// <summary>A follow-up event a receptor returns, with its own void receptor proving the cascade ran here.</summary>
public record ExFollowUpEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

public sealed class ExFollowUpEventReceptor : IReceptor<ExFollowUpEvent> {
  public ValueTask HandleAsync(ExFollowUpEvent message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExFollowUpEventReceptor), message.Id);
    return ValueTask.CompletedTask;
  }
}

// ── E1: an event whose receptor returns a result ─────────────────────────────────────────────

public record ExTypedEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

public sealed class ExTypedEventReceptor : IReceptor<ExTypedEvent, ExFollowUpEvent> {
  public ValueTask<ExFollowUpEvent> HandleAsync(ExTypedEvent message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExTypedEventReceptor), message.Id);
    return ValueTask.FromResult(new ExFollowUpEvent(message.Id));
  }
}

// ── E1b: a typed event receptor declared at a pipeline stage, response object ────────────────

public record ExPipelineObjectEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

[FireAt(LifecycleStage.PostInboxInline)]
public sealed class ExPipelineObjectEventReceptor : IReceptor<ExPipelineObjectEvent, object> {
  public ValueTask<object> HandleAsync(ExPipelineObjectEvent message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExPipelineObjectEventReceptor), message.Id);
    return ValueTask.FromResult<object>(new ExFollowUpEvent(message.Id));
  }
}

// ── E2 / E3: sync receptors ──────────────────────────────────────────────────────────────────

public record ExVoidSyncEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

public sealed class ExVoidSyncEventReceptor : ISyncReceptor<ExVoidSyncEvent> {
  public void Handle(ExVoidSyncEvent message) => PolicyProbe.Record(nameof(ExVoidSyncEventReceptor), message.Id);
}

public record ExResultSyncEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

public sealed class ExResultSyncEventReceptor : ISyncReceptor<ExResultSyncEvent, ExFollowUpEvent> {
  public ExFollowUpEvent Handle(ExResultSyncEvent message) {
    PolicyProbe.Record(nameof(ExResultSyncEventReceptor), message.Id);
    return new ExFollowUpEvent(message.Id);
  }
}

public record ExOwnedVoidSyncCommand(Guid Id) : ICommand, IPolicyProbeMessage;

public sealed class ExOwnedVoidSyncCommandReceptor : ISyncReceptor<ExOwnedVoidSyncCommand> {
  public void Handle(ExOwnedVoidSyncCommand message) => PolicyProbe.Record(nameof(ExOwnedVoidSyncCommandReceptor), message.Id);
}

// ── E4: polymorphic receptors ────────────────────────────────────────────────────────────────

public interface IExShapeEvent : IEvent, IPolicyProbeMessage;

public record ExCircleEvent([property: StreamId] Guid Id) : IExShapeEvent;

public sealed class ExShapeEventReceptor : IReceptor<IExShapeEvent> {
  public ValueTask HandleAsync(IExShapeEvent message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExShapeEventReceptor), message.Id);
    return ValueTask.CompletedTask;
  }
}

public interface IExShapeCommand : ICommand, IPolicyProbeMessage;

public record ExSquareCommand(Guid Id) : IExShapeCommand;

public sealed class ExShapeCommandReceptor : IReceptor<IExShapeCommand> {
  public ValueTask HandleAsync(IExShapeCommand message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExShapeCommandReceptor), message.Id);
    return ValueTask.CompletedTask;
  }
}

// ── E5: a plain void command and event, dispatched through a generic overload at an interface type ──

public record ExVoidCommand(Guid Id) : ICommand, IPolicyProbeMessage;

public sealed class ExVoidCommandReceptor : IReceptor<ExVoidCommand> {
  public ValueTask HandleAsync(ExVoidCommand message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExVoidCommandReceptor), message.Id);
    return ValueTask.CompletedTask;
  }
}

public record ExVoidEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

public sealed class ExVoidEventReceptor : IReceptor<ExVoidEvent> {
  public ValueTask HandleAsync(ExVoidEvent message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExVoidEventReceptor), message.Id);
    return ValueTask.CompletedTask;
  }
}

// ── E7b: an owned command whose only receptor is declared at a pipeline stage ────────────────

public record ExPipelineOnlyCommand(Guid Id) : ICommand, IPolicyProbeMessage;

[FireAt(LifecycleStage.PostInboxInline)]
public sealed class ExPipelineOnlyCommandReceptor : IReceptor<ExPipelineOnlyCommand> {
  public ValueTask HandleAsync(ExPipelineOnlyCommand message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExPipelineOnlyCommandReceptor), message.Id);
    return ValueTask.CompletedTask;
  }
}

// ── E8: a receptor that routes its result ────────────────────────────────────────────────────

public record ExRouteLocalTrigger(Guid Id) : ICommand, IPolicyProbeMessage;

public sealed class ExRouteLocalTriggerReceptor : IReceptor<ExRouteLocalTrigger, Routed<ExFollowUpEvent>> {
  public ValueTask<Routed<ExFollowUpEvent>> HandleAsync(ExRouteLocalTrigger message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(Route.Local(new ExFollowUpEvent(message.Id)));
}

public record ExRouteOutboxTrigger(Guid Id) : ICommand, IPolicyProbeMessage;

public sealed class ExRouteOutboxTriggerReceptor : IReceptor<ExRouteOutboxTrigger, Routed<ExFollowUpEvent>> {
  public ValueTask<Routed<ExFollowUpEvent>> HandleAsync(ExRouteOutboxTrigger message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(Route.Outbox(new ExFollowUpEvent(message.Id)));
}

// ── E10: a receptor registered at runtime that returns a result ──────────────────────────────

public record ExRuntimeEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

public record ExRuntimeCommand(Guid Id) : ICommand, IPolicyProbeMessage;

/// <summary>Generic, so the generator skips it: it exists only when a test registers it at runtime.</summary>
public sealed class ExRuntimeResultReceptor<TMessage> : IReceptor<TMessage, ExFollowUpEvent> where TMessage : IPolicyProbeMessage {
  public ValueTask<ExFollowUpEvent> HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExRuntimeResultReceptor<TMessage>), message.Id);
    return ValueTask.FromResult(new ExFollowUpEvent(message.Id));
  }
}

// ── E6: request/response receptors reached through every LocalInvoke overload ─────────────────

public record ExRpcResponse(Guid Id);

public record ExRpcCommand(Guid Id) : ICommand, IPolicyProbeMessage;

public sealed class ExRpcCommandReceptor : IReceptor<ExRpcCommand, (ExRpcResponse, ExFollowUpEvent)> {
  public ValueTask<(ExRpcResponse, ExFollowUpEvent)> HandleAsync(ExRpcCommand message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExRpcCommandReceptor), message.Id);
    return ValueTask.FromResult((new ExRpcResponse(message.Id), new ExFollowUpEvent(message.Id)));
  }
}

public record ExSyncRpcCommand(Guid Id) : ICommand, IPolicyProbeMessage;

public sealed class ExSyncRpcCommandReceptor : ISyncReceptor<ExSyncRpcCommand, ExRpcResponse> {
  public ExRpcResponse Handle(ExSyncRpcCommand message) {
    PolicyProbe.Record(nameof(ExSyncRpcCommandReceptor), message.Id);
    return new ExRpcResponse(message.Id);
  }
}

// ── E13: a receptor's [DefaultRouting] applies at a worker stage too ────────────────────────────

public record ExDefaultLocalTrigger(Guid Id) : ICommand, IPolicyProbeMessage;

[DefaultRouting(DispatchModes.Local)]
public sealed class ExDefaultLocalTriggerReceptor : IReceptor<ExDefaultLocalTrigger, ExFollowUpEvent> {
  public ValueTask<ExFollowUpEvent> HandleAsync(ExDefaultLocalTrigger message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(new ExFollowUpEvent(message.Id));
}

// ── E21: a routed result whose type no receptor declares goes through the dynamic outbox path ──

public record ExUndeclaredEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

public sealed class ExUndeclaredEventReceptor : IReceptor<ExUndeclaredEvent> {
  public ValueTask HandleAsync(ExUndeclaredEvent message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExUndeclaredEventReceptor), message.Id);
    return ValueTask.CompletedTask;
  }
}

public record ExObjectRouteOutboxTrigger(Guid Id) : ICommand, IPolicyProbeMessage;

public sealed class ExObjectRouteOutboxTriggerReceptor : IReceptor<ExObjectRouteOutboxTrigger, object> {
  public ValueTask<object> HandleAsync(ExObjectRouteOutboxTrigger message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult<object>(Route.Outbox(new ExUndeclaredEvent(message.Id)));
}

// ── E28: the caller's explicit context reaches the receptor ────────────────────────────────────

public record ExContextProbeCommand(Guid Id) : ICommand, IPolicyProbeMessage;

public sealed class ExContextProbeCommandReceptor : IReceptor<ExContextProbeCommand> {
  private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid?> _seenCorrelation = new();

  /// <summary>The correlation id the receptor saw in the ambient message context for a message, if any.</summary>
  public static Guid? SeenCorrelation(Guid id) => _seenCorrelation.TryGetValue(id, out var seen) ? seen : null;

  public ValueTask HandleAsync(ExContextProbeCommand message, CancellationToken cancellationToken = default) {
    _seenCorrelation[message.Id] = Whizbang.Core.Security.MessageContextAccessor.CurrentContext?.CorrelationId.Value;
    return ValueTask.CompletedTask;
  }
}

public record ExRpcDefaultLocalCommand(Guid Id) : ICommand, IPolicyProbeMessage;

[DefaultRouting(DispatchModes.Local)]
public sealed class ExRpcDefaultLocalCommandReceptor : IReceptor<ExRpcDefaultLocalCommand, (ExRpcResponse, ExFollowUpEvent)> {
  public ValueTask<(ExRpcResponse, ExFollowUpEvent)> HandleAsync(ExRpcDefaultLocalCommand message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExRpcDefaultLocalCommandReceptor), message.Id);
    return ValueTask.FromResult((new ExRpcResponse(message.Id), new ExFollowUpEvent(message.Id)));
  }
}

// ── A returned value that is neither a command nor an event, owned, with no handler ────────────

public sealed record ExPlainResult(Guid Id) : IMessage;

public record ExPlainResultTrigger(Guid Id) : ICommand, IPolicyProbeMessage;

public sealed class ExPlainResultTriggerReceptor : IReceptor<ExPlainResultTrigger, ExPlainResult> {
  public ValueTask<ExPlainResult> HandleAsync(ExPlainResultTrigger message, CancellationToken cancellationToken = default) {
    PolicyProbe.Record(nameof(ExPlainResultTriggerReceptor), message.Id);
    return ValueTask.FromResult(new ExPlainResult(message.Id));
  }
}

// ── E12: a result routed LocalNoPersist never reaches a perspective, so nothing waits on it ─────

public record ExNoPersistTrigger(Guid Id) : ICommand, IPolicyProbeMessage;

public sealed class ExNoPersistTriggerReceptor : IReceptor<ExNoPersistTrigger, Routed<ExFollowUpEvent>> {
  public ValueTask<Routed<ExFollowUpEvent>> HandleAsync(ExNoPersistTrigger message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(Route.LocalNoPersist(new ExFollowUpEvent(message.Id)));
}
