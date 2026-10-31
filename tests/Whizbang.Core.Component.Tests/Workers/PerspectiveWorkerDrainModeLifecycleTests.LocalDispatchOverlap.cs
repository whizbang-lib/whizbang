// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1918: a local dispatch of an event this process is also projecting runs its own lifecycle. The dispatch and
/// the perspective worker each track the event, and one path ending its tracking never ends the other's, so the
/// worker still fires PostAllPerspectives and PostLifecycle for the event, and the dispatch still fires its own
/// PostLifecycle.
/// </summary>
public partial class PerspectiveWorkerDrainModeLifecycleTests {
  /// <summary>Records every stage it is asked to run, per event.</summary>
  private sealed class StageRecordingInvoker : IReceptorInvoker {
    private readonly ConcurrentBag<(Guid EventId, LifecycleStage Stage)> _invocations = [];

    public int CountFor(Guid eventId, LifecycleStage stage) =>
      _invocations.Count(i => i.EventId == eventId && i.Stage == stage);

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      _invocations.Add((envelope.MessageId.Value, stage));
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// A registry holding one PostLifecycle receptor for <see cref="AlphaEvent"/>, which is what makes the local
  /// dispatch run its lifecycle stages; the recording invoker stands in for running it.
  /// </summary>
  private sealed class PostLifecycleReceptorRegistry : IReceptorRegistry {
    private static readonly ReceptorInfo[] _postLifecycle = [
      new(MessageType: typeof(AlphaEvent), ReceptorId: "post_lifecycle_alpha",
        InvokeAsync: (_, _, _, _, _) => ValueTask.FromResult<object?>(null))
    ];

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
      messageType == typeof(AlphaEvent) && stage == LifecycleStage.PostLifecycleInline ? _postLifecycle : [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  /// <summary>
  /// A dispatcher whose only receptor handles <see cref="AlphaEvent"/> by running <paramref name="onHandled"/>,
  /// which is handed the id of the envelope the dispatch built for the event.
  /// </summary>
  private sealed class OverlapDispatcher(IServiceProvider sp, Action<Guid> onHandled)
      : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
        receptorRegistry: sp.GetRequiredService<IReceptorRegistry>()) {
    private readonly IEnvelopeRegistry _envelopes = sp.GetRequiredService<IEnvelopeRegistry>();

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      if (messageType != typeof(AlphaEvent)) {
        return null;
      }
      return msg => {
        var envelope = _envelopes.TryGetEnvelope(msg)!;
        onHandled(envelope.MessageId.Value);
        return ValueTask.CompletedTask;
      };
    }

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;
  }

  [Test]
  [Timeout(30000)]
  public async Task LocalDispatchOfAnEventTheWorkerIsProjecting_BothPathsCompleteTheirOwnLifecycleAsync(CancellationToken cancellationToken) {
    Guid streamId = TrackedGuid.New();
    var (worker, _, _, _, _, lc, _) = _createWorkerWithLifecycle(_twoAlphaPerspectives(), [], [], [streamId]);
    var invoker = new StageRecordingInvoker();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddWhizbangMessageSecurity(options => options.AllowAnonymous = true);
    services.AddSingleton(lc);
    services.AddSingleton<IEnvelopeRegistry, EnvelopeRegistry>();
    services.AddSingleton<IReceptorRegistry, PostLifecycleReceptorRegistry>();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    await using var provider = services.BuildServiceProvider();
    var batch = new ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();

    // The worker is projecting the event when the local dispatch of it runs: it began tracking the event at
    // PrePerspective and both perspectives reported, so only its terminal stages are left.
    var dispatcher = new OverlapDispatcher(provider, eventId => {
      var projected = _createEnvelope(eventId, new AlphaEvent("projected"));
      lc.BeginTracking(eventId, projected, LifecycleStage.PrePerspectiveDetached, MessageSource.Local, streamId);
      lc.ExpectPerspectiveCompletions(eventId, [FIRST_PERSPECTIVE, SECOND_PERSPECTIVE]);
      lc.SignalPerspectiveComplete(eventId, FIRST_PERSPECTIVE);
      lc.SignalPerspectiveComplete(eventId, SECOND_PERSPECTIVE);
      batch[eventId] = (projected, streamId);
    });

    await dispatcher.LocalInvokeAsync(new AlphaEvent("dispatched"));
    var eventId = batch.Keys.Single();
    await worker.FirePostLifecycleDetachedAsync(batch, lc, invoker, [], provider, cancellationToken);
    await ((LifecycleCoordinator)lc).DrainAllDetachedAsync();

    await Assert.That(invoker.CountFor(eventId, LifecycleStage.PostAllPerspectivesInline)).IsEqualTo(1)
      .Because("the worker's PostAllPerspectives always fires; the dispatch ending its own tracking must not end the worker's");
    await Assert.That(invoker.CountFor(eventId, LifecycleStage.PostAllPerspectivesDetached)).IsEqualTo(1);
    await Assert.That(invoker.CountFor(eventId, LifecycleStage.PostLifecycleInline)).IsEqualTo(2)
      .Because("each path completes its own lifecycle: once for the local dispatch, once for the projection");
    await Assert.That(invoker.CountFor(eventId, LifecycleStage.PostLifecycleDetached)).IsEqualTo(2);
    await Assert.That(((LifecycleCoordinator)lc).TrackedCount).IsEqualTo(0)
      .Because("each path ends the tracking it began, and only that");
  }
}
