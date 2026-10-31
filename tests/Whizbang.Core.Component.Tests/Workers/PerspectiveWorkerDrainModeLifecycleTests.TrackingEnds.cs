// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1797: the perspective worker begins lifecycle tracking for every event it projects, so it also ends it.
/// An event whose lifecycle has completed leaves the coordinator at that exit, also when a terminal-stage
/// receptor fails or the stages are canceled; an event still waiting on a perspective stays tracked.
/// </summary>
public partial class PerspectiveWorkerDrainModeLifecycleTests {
  private const string FIRST_PERSPECTIVE = "Perspective1";
  private const string SECOND_PERSPECTIVE = "Perspective2";

  private static List<PerspectiveRegistrationInfo> _twoAlphaPerspectives() => [
    new(FIRST_PERSPECTIVE, "global::Test.Perspective1", "global::Test.Model1", [TypeNameFormatter.Format(typeof(AlphaEvent))]),
    new(SECOND_PERSPECTIVE, "global::Test.Perspective2", "global::Test.Model2", [TypeNameFormatter.Format(typeof(AlphaEvent))])
  ];

  private static (List<StreamEventData> Raw, List<MessageEnvelope<IEvent>> Typed, List<Guid> Ids) _alphaEvents(Guid streamId, int count) {
    var raw = new List<StreamEventData>();
    var typed = new List<MessageEnvelope<IEvent>>();
    var ids = new List<Guid>();
    for (var i = 0; i < count; i++) {
      Guid eventId = TrackedGuid.New();
      ids.Add(eventId);
      raw.Add(_createRawEvent(streamId, eventId, typeof(AlphaEvent), """{"Data":"tracked"}"""));
      typed.Add(_createEnvelope(eventId, new AlphaEvent("tracked")));
    }
    return (raw, typed, ids);
  }

  /// <summary>Throws at one stage; records every stage it was asked to run.</summary>
  private sealed class ThrowingAtStageInvoker(LifecycleStage throwAt, Func<Exception> exception) : IReceptorInvoker {
    private readonly ConcurrentBag<(Guid EventId, LifecycleStage Stage)> _invocations = [];

    public int CountForStage(LifecycleStage stage) => _invocations.Count(i => i.Stage == stage);

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      _invocations.Add((envelope.MessageId.Value, stage));
      if (stage == throwAt) {
        throw exception();
      }
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_EveryProjectedEventsTracking_EndsOnceItsLifecycleCompletesAsync(CancellationToken cancellationToken) {
    Guid streamId = TrackedGuid.New();
    var (raw, typed, ids) = _alphaEvents(streamId, 5);
    var (worker, coordinator, _, _, invoker, lc, harness) = _createWorkerWithLifecycle(_twoAlphaPerspectives(), raw, typed, [streamId]);

    await _runWorkerOneBatchAsync(worker, coordinator, harness, cancellationToken);

    await Assert.That(invoker.CountForStage(LifecycleStage.PostAllPerspectivesInline)).IsEqualTo(5)
      .Because("every event's PostAllPerspectives waiter still sees completion before its tracking ends");
    await Assert.That(invoker.CountForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(5);
    await Assert.That(((LifecycleCoordinator)lc).TrackedCount).IsEqualTo(0)
      .Because("a completed lifecycle leaves the coordinator at its exit; kept, every projected event grew the heap");
    foreach (var id in ids) {
      await Assert.That(lc.GetTracking(LifecycleTrackingPath.Perspective, id)).IsNull();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_AnEventWaitingOnAPerspectiveThatHasNotReported_StaysTrackedAsync(CancellationToken cancellationToken) {
    Guid streamId = TrackedGuid.New();
    var (raw, typed, ids) = _alphaEvents(streamId, 1);
    var (worker, coordinator, registry, _, invoker, lc, harness) = _createWorkerWithLifecycle(_twoAlphaPerspectives(), raw, typed, [streamId]);
    registry.SilentPerspectives.Add(SECOND_PERSPECTIVE);

    await _runWorkerOneBatchAsync(worker, coordinator, harness, cancellationToken);

    await Assert.That(invoker.HasStage(LifecycleStage.PostAllPerspectivesInline)).IsFalse()
      .Because("one expected perspective has not reported, so the WhenAll gate is still closed");
    await Assert.That(lc.GetTracking(LifecycleTrackingPath.Perspective, ids[0])).IsNotNull()
      .Because("an entry a later batch can still complete is never ended early; its stage guard and signals live in it");
    await Assert.That(((LifecycleCoordinator)lc).TrackedCount).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_APostAllPerspectivesReceptorThatThrows_StillEndsItsEventsTrackingAsync(CancellationToken cancellationToken) {
    Guid streamId = TrackedGuid.New();
    var (raw, typed, ids) = _alphaEvents(streamId, 2);
    var throwing = new ThrowingAtStageInvoker(LifecycleStage.PostAllPerspectivesInline, () => new InvalidOperationException("receptor failed"));
    var (worker, coordinator, _, _, _, lc, harness) = _createWorkerWithLifecycle(
      _twoAlphaPerspectives(), raw, typed, [streamId], customInvoker: throwing);

    await _runWorkerOneBatchAsync(worker, coordinator, harness, cancellationToken);

    await Assert.That(throwing.CountForStage(LifecycleStage.PostAllPerspectivesInline)).IsEqualTo(2)
      .Because("a failing receptor is isolated per event, so both events reached the stage");
    await Assert.That(((LifecycleCoordinator)lc).TrackedCount).IsEqualTo(0)
      .Because("a failed terminal stage is not retried in memory, so its entry ends too; the durable marker drives the replay");
    foreach (var id in ids) {
      await Assert.That(lc.GetTracking(LifecycleTrackingPath.Perspective, id)).IsNull();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task FirePostLifecycle_CanceledMidBatch_EndsEveryReadyEventsTrackingAsync(CancellationToken cancellationToken) {
    Guid streamId = TrackedGuid.New();
    var (raw, typed, ids) = _alphaEvents(streamId, 2);
    var canceling = new ThrowingAtStageInvoker(LifecycleStage.PostAllPerspectivesInline, () => new OperationCanceledException("stopping"));
    var (worker, _, _, _, _, lc, _) = _createWorkerWithLifecycle(_twoAlphaPerspectives(), raw, typed, [streamId]);
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IReceptorInvoker>(_ => canceling);
    await using var provider = services.BuildServiceProvider();
    var batch = new ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();
    foreach (var envelope in typed) {
      var id = envelope.MessageId.Value;
      lc.BeginTracking(LifecycleTrackingPath.Perspective, id, envelope, LifecycleStage.PrePerspectiveDetached, MessageSource.Local, streamId);
      lc.ExpectPerspectiveCompletions(id, [FIRST_PERSPECTIVE]);
      lc.SignalPerspectiveComplete(id, FIRST_PERSPECTIVE);
      batch[id] = (envelope, streamId);
    }

    await Assert.That(async () => await worker.FirePostLifecycleDetachedAsync(batch, lc, canceling, [], provider, cancellationToken))
      .Throws<OperationCanceledException>()
      .Because("cancellation still ends the stages; it is not swallowed as a receptor failure");

    await Assert.That(canceling.CountForStage(LifecycleStage.PostAllPerspectivesInline)).IsEqualTo(1)
      .Because("the cancellation stopped the batch at its first event");
    await Assert.That(((LifecycleCoordinator)lc).TrackedCount).IsEqualTo(0)
      .Because("the canceled event and the ready event after it both end; nothing in memory will finish them");
    foreach (var id in ids) {
      await Assert.That(lc.GetTracking(LifecycleTrackingPath.Perspective, id)).IsNull();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task FirePostLifecycle_AnEventWhoseTrackingEnded_IsNeitherRegisteredNorFiredAgainAsync(CancellationToken cancellationToken) {
    Guid streamId = TrackedGuid.New();
    var (raw, typed, ids) = _alphaEvents(streamId, 1);
    var (worker, coordinator, _, _, invoker, lc, harness) = _createWorkerWithLifecycle(_twoAlphaPerspectives(), raw, typed, [streamId]);
    await _runWorkerOneBatchAsync(worker, coordinator, harness, cancellationToken);
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    await using var provider = services.BuildServiceProvider();
    var cooledAgain = new ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> {
      [ids[0]] = (typed[0], streamId)
    };

    // A cooled row re-signals its event into a later batch after the event's lifecycle has ended.
    await worker.FirePostLifecycleDetachedAsync(cooledAgain, lc, invoker, [], provider, cancellationToken);

    await Assert.That(invoker.CountForStage(LifecycleStage.PostAllPerspectivesInline)).IsEqualTo(1)
      .Because("an ended lifecycle has no tracking to fire again");
    await Assert.That(lc.AreAllPerspectivesComplete(ids[0])).IsTrue()
      .Because("no WhenAll expectation is registered for an untracked event; one would never complete and never be swept");
    await Assert.That(((LifecycleCoordinator)lc).TrackedCount).IsEqualTo(0);
  }
}
