// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Where <see cref="SystemEventEmitter"/> gets what one record needs: the audit decision hook to consult and
/// the event store to append to. How they are reached can then differ from how the emitter is registered.
/// </summary>
/// <docs>fundamentals/events/system-events#emitter</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventEmitterRegistrationTests.cs</tests>
internal interface ISystemEventSink {
  /// <summary>Opens what one record needs. The caller disposes it once the record is appended or dropped.</summary>
  ISystemEventSinkSession Open();
}

/// <summary>What one record needs: the hook to consult and the append.</summary>
/// <docs>fundamentals/events/system-events#emitter</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventEmitterRegistrationTests.cs</tests>
internal interface ISystemEventSinkSession : IAsyncDisposable {
  /// <summary>The audit decision hook for this record; null leaves the decision to the attribute.</summary>
  IAuditDecisionHook? AuditDecisionHook { get; }

  /// <summary>Appends one system event envelope to <paramref name="streamId"/>.</summary>
  Task AppendAsync<TSystemEvent>(Guid streamId, MessageEnvelope<TSystemEvent> envelope, CancellationToken cancellationToken);
}

/// <summary>
/// The store and hook the emitter was given. Used by the emitter's public constructor, so the emitter
/// lives no longer than that store; every record shares them and disposing a session disposes nothing.
/// </summary>
/// <docs>fundamentals/events/system-events#emitter</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventEmitterTests.cs</tests>
internal sealed class EventStoreSystemEventSink(IEventStore store, IAuditDecisionHook? auditDecisionHook)
    : ISystemEventSink, ISystemEventSinkSession {
  public IAuditDecisionHook? AuditDecisionHook => auditDecisionHook;

  public ISystemEventSinkSession Open() => this;

  public Task AppendAsync<TSystemEvent>(Guid streamId, MessageEnvelope<TSystemEvent> envelope, CancellationToken cancellationToken) =>
    store.AppendAsync(streamId, envelope, cancellationToken);

  public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Opens a scope of its own for each record, resolves the hook and the event store from it, and disposes it
/// once the record is appended or dropped.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets the default emitter be a singleton whatever the lifetimes of the store and the hook. A
/// singleton that held a scoped store (an EF Core store holds a DbContext) or a scoped hook would be a captive
/// dependency; a scoped emitter would share the caller's DbContext, and a fire-and-forget record (a denied
/// read) would then race the caller's own queries on it and could outlive the request that owns it.
/// </para>
/// <para>
/// A record appended this way commits on its own rather than in the caller's unit of work, which is
/// what a security or audit record wants: a denial is still recorded when the work that was denied
/// rolls back.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/system-events#emitter</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventEmitterRegistrationTests.cs:AddWhizbang_WithAScopedEventStoreRegisteredAfterIt_NeverCapturesTheStoreAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventEmitterRegistrationTests.cs:AddWhizbang_TheDefaultEmitter_OutlivesTheScopeItWasResolvedInAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventEmitterRegistrationTests.cs:AddWhizbang_AScopedHostHook_IsHonoredWithoutBeingCapturedAsync</tests>
internal sealed class ScopePerAppendSystemEventSink(IServiceScopeFactory scopeFactory) : ISystemEventSink {
  public ISystemEventSinkSession Open() => new Session(scopeFactory.CreateAsyncScope());

  private sealed class Session(AsyncServiceScope scope) : ISystemEventSinkSession {
    public IAuditDecisionHook? AuditDecisionHook => scope.ServiceProvider.GetService<IAuditDecisionHook>();

    public Task AppendAsync<TSystemEvent>(Guid streamId, MessageEnvelope<TSystemEvent> envelope, CancellationToken cancellationToken) =>
      scope.ServiceProvider.GetRequiredService<IEventStore>().AppendAsync(streamId, envelope, cancellationToken);

    public ValueTask DisposeAsync() => scope.DisposeAsync();
  }
}
