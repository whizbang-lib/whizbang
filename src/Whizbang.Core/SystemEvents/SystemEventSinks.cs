// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Where <see cref="SystemEventEmitter"/> appends a record: the one append the emitter needs from an
/// event store, so how the store is reached can differ from how the emitter is registered.
/// </summary>
/// <docs>fundamentals/events/system-events#emitter</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventEmitterRegistrationTests.cs</tests>
internal interface ISystemEventSink {
  /// <summary>Appends one system event envelope to <paramref name="streamId"/>.</summary>
  Task AppendAsync<TSystemEvent>(Guid streamId, MessageEnvelope<TSystemEvent> envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Appends to an event store the emitter was given. Used by the emitter's public constructor, so the
/// emitter lives no longer than that store.
/// </summary>
/// <docs>fundamentals/events/system-events#emitter</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventEmitterTests.cs</tests>
internal sealed class EventStoreSystemEventSink(IEventStore store) : ISystemEventSink {
  public Task AppendAsync<TSystemEvent>(Guid streamId, MessageEnvelope<TSystemEvent> envelope, CancellationToken cancellationToken) =>
    store.AppendAsync(streamId, envelope, cancellationToken);
}

/// <summary>
/// Appends each record through an event store resolved in a scope of its own, created for that record
/// and disposed once it is appended.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets the default emitter be a singleton whatever the store's lifetime. A singleton that
/// held a scoped store (an EF Core store holds a DbContext) would be a captive dependency; a scoped
/// emitter would share the caller's DbContext, and a fire-and-forget record (a denied read) would then
/// race the caller's own queries on it and could outlive the request that owns it.
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
internal sealed class ScopePerAppendSystemEventSink(IServiceScopeFactory scopeFactory) : ISystemEventSink {
  public async Task AppendAsync<TSystemEvent>(Guid streamId, MessageEnvelope<TSystemEvent> envelope, CancellationToken cancellationToken) {
    var scope = scopeFactory.CreateAsyncScope();
    try {
      await scope.ServiceProvider.GetRequiredService<IEventStore>().AppendAsync(streamId, envelope, cancellationToken);
    } finally {
      await scope.DisposeAsync();
    }
  }
}
