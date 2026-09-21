#pragma warning disable CA1707

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A cascaded event is stored before the events its own receptors cascade, so a stream replays cause
/// before effect.
/// </summary>
/// <remarks>
/// <para>
/// <c>CascadeMessageAsync</c> used to run local dispatch first and store the event afterwards. A
/// receptor fired by that dispatch cascades further events, and each of those stores ITSELF on the
/// way through, so the cause was assigned a LATER stream version than its own effects. Observed in a
/// consuming application as a job stream whose initialising event sat at version 4, behind the status
/// initialisation and version bump that event had caused and behind a sibling field update:
/// <c>[v1=StatusInitialized, v2=VersionBumped, v3=NameUpdated, v4=Initialized]</c>. Anything replaying
/// that stream in version order acts on a job it has not been told exists.
/// </para>
/// <para>
/// The ordering is asserted on the STORE seam rather than on wall-clock or on the dispatch seam,
/// because the store is what assigns the version a consumer later replays.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/message-cascade#event-store-only</docs>
[Category("Dispatcher")]
public class DispatcherCascadeStoresCauseBeforeEffectTests {

  private sealed record _cause(string Id) : IEvent;
  private sealed record _effect(string Id) : IEvent;

  private sealed class _scopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new _scope(provider);
    private sealed class _scope(IServiceProvider provider) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = provider;
      public void Dispose() { }
    }
  }

  /// <summary>
  /// Records the order events reach the event-store seam, and gives <c>_cause</c> a receptor that
  /// cascades an <c>_effect</c> — the shape that exposes the inversion. A composite of inert events
  /// cannot show it, which is why the original case needed a receptor cascade to reproduce.
  /// </summary>
  private sealed class _orderRecordingDispatcher(IServiceProvider sp)
      : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null)) {
    public List<string> Stored { get; } = [];

    protected override Task CascadeToEventStoreOnlyAsync(
        IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      Stored.Add(message switch {
        _cause c => $"cause:{c.Id}",
        _effect e => $"effect:{e.Id}",
        _ => messageType.Name,
      });
      return Task.CompletedTask;
    }

    protected override Task CascadeToOutboxAsync(
        IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) =>
      Task.CompletedTask;

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) {
      if (eventType != typeof(_cause)) {
        return null;
      }
      return async (msg, env, ct) => {
        var cause = (_cause)msg;
        await CascadeMessageAsync(new _effect(cause.Id), env, DispatchModes.Local, ct);
      };
    }

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => _ => Task.CompletedTask;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;
  }

  private static _orderRecordingDispatcher _build() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(sp => new _scopeFactory(sp));
    services.AddSingleton<IWorkCoordinator>(new NoOpWorkCoordinator());
    var sp = services.BuildServiceProvider();
    return new _orderRecordingDispatcher(sp);
  }

  [Test]
  public async Task ACascadedEvent_IsStoredBeforeTheEventsItsReceptorCascadesAsync() {
    var dispatcher = _build();

    await dispatcher.CascadeMessageAsync(new _cause("a"), sourceEnvelope: null, DispatchModes.Local);

    // Compared as one ORDERED string, deliberately. A collection-equivalence assertion here passes
    // against the defect: it treats ["effect:a", "cause:a"] as equivalent to ["cause:a", "effect:a"],
    // which is precisely the distinction this case exists to make.
    await Assert.That(string.Join(" -> ", dispatcher.Stored)).IsEqualTo("cause:a -> effect:a")
      .Because("the cause has to reach the store before the effect its receptor cascaded, or the stream "
        + "hands a consumer the effect of an event it has not been told about.");
  }

  /// <summary>
  /// Siblings keep producer order, and each one's effects are stored under it rather than drifting
  /// ahead of the sibling that follows.
  /// </summary>
  [Test]
  public async Task Siblings_KeepProducerOrder_WithEachCauseAheadOfItsOwnEffectAsync() {
    var dispatcher = _build();

    foreach (var id in new[] { "first", "second" }) {
      await dispatcher.CascadeMessageAsync(new _cause(id), sourceEnvelope: null, DispatchModes.Local);
    }

    await Assert.That(string.Join(" -> ", dispatcher.Stored))
      .IsEqualTo("cause:first -> effect:first -> cause:second -> effect:second")
      .Because("this is the composite fan-out shape: inner events cascaded in producer order, each one's "
        + "own cascade settled before the next.");
  }
}
