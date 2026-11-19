// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Each path tracks an event in an entry of its own (#1918). A local dispatch of an event this process also projects
/// and publishes is tracked once per path: each entry has its own stage guard and its own end, and only the
/// perspective path's end closes the event's perspective gate.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleCoordinator.cs</code-under-test>
[Category("Core")]
[Category("Lifecycle")]
public class LifecycleTrackingPathTests {
  private const string PERSPECTIVE_A = "Test.APerspective";
  private const string PERSPECTIVE_B = "Test.BPerspective";

  private static readonly TimeSpan _bound = TimeSpan.FromMinutes(5);

  private sealed record PathEvent(Guid Id) : IEvent;

  private static MessageEnvelope<PathEvent> _envelope(Guid id) => new() {
    MessageId = MessageId.From(id),
    Payload = new PathEvent(id),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private sealed class NoOpInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(
        IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private static ServiceProvider _provider() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IReceptorInvoker, NoOpInvoker>();
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    return services.BuildServiceProvider();
  }

  private static ILifecycleTracking _begin(LifecycleCoordinator coordinator, LifecycleTrackingPath path, Guid eventId) =>
    coordinator.BeginTracking(path, eventId, _envelope(eventId), LifecycleStage.PrePerspectiveDetached, MessageSource.Local);

  [Test]
  public async Task EachPathTracksTheSameEventInAnEntryOfItsOwnAsync(CancellationToken cancellationToken) {
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance);
    Guid eventId = TrackedGuid.New();
    var dispatch = _begin(coordinator, LifecycleTrackingPath.LocalDispatch, eventId);
    var projection = _begin(coordinator, LifecycleTrackingPath.Perspective, eventId);
    await using var provider = _provider();

    await dispatch.AdvanceToAsync(LifecycleStage.PostLifecycleInline, provider, cancellationToken);

    await Assert.That(ReferenceEquals(dispatch, projection)).IsFalse()
      .Because("one entry shared by two paths lets the first path's PostLifecycle complete the other's lifecycle");
    await Assert.That(dispatch.IsComplete).IsTrue();
    await Assert.That(projection.IsComplete).IsFalse()
      .Because("the dispatch completing its lifecycle leaves the projection's stage guard untouched");
    await Assert.That(_begin(coordinator, LifecycleTrackingPath.Perspective, eventId)).IsSameReferenceAs(projection)
      .Because("the same path asking again for the same event gets its own entry back");
    await Assert.That(coordinator.TrackedCount).IsEqualTo(2);
  }

  [Test]
  public async Task EndingOnePathsTracking_NeverEndsAnotherPathsTrackingOfTheSameEventAsync() {
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance);
    Guid eventId = TrackedGuid.New();
    var projection = _begin(coordinator, LifecycleTrackingPath.Perspective, eventId);
    _begin(coordinator, LifecycleTrackingPath.LocalDispatch, eventId);
    _begin(coordinator, LifecycleTrackingPath.Outbox, eventId);

    coordinator.AbandonTracking(LifecycleTrackingPath.LocalDispatch, eventId);
    coordinator.AbandonTracking(LifecycleTrackingPath.Outbox, eventId);

    await Assert.That(coordinator.GetTracking(LifecycleTrackingPath.Perspective, eventId)).IsSameReferenceAs(projection)
      .Because("a path ends only the tracking it began");
    await Assert.That(coordinator.GetTracking(LifecycleTrackingPath.LocalDispatch, eventId)).IsNull();
    await Assert.That(coordinator.GetTracking(LifecycleTrackingPath.Outbox, eventId)).IsNull();
    await Assert.That(coordinator.TrackedCount).IsEqualTo(1);
  }

  [Test]
  public async Task EndingAnotherPathsTracking_LeavesThePerspectiveGateClosedAsync() {
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance);
    Guid eventId = TrackedGuid.New();
    _begin(coordinator, LifecycleTrackingPath.Perspective, eventId);
    coordinator.ExpectPerspectiveCompletions(eventId, [PERSPECTIVE_A, PERSPECTIVE_B]);
    coordinator.SignalPerspectiveComplete(eventId, PERSPECTIVE_A);
    _begin(coordinator, LifecycleTrackingPath.LocalDispatch, eventId);

    coordinator.AbandonTracking(LifecycleTrackingPath.LocalDispatch, eventId);

    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsFalse()
      .Because("dropped by another path's end, the gate would read as no expectations and fire with a perspective still to report");

    coordinator.AbandonTracking(LifecycleTrackingPath.Perspective, eventId);

    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsTrue()
      .Because("the gate belongs to the perspective path's entry and ends with it");
  }

  [Test]
  public async Task Sweep_EndingAnotherPathsStaleEntry_LeavesThePerspectiveGateClosedAsync() {
    var clock = new FakeTimeProvider();
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance, timeProvider: clock);
    Guid eventId = TrackedGuid.New();
    _begin(coordinator, LifecycleTrackingPath.LocalDispatch, eventId);
    clock.Advance(_bound + TimeSpan.FromSeconds(1));
    _begin(coordinator, LifecycleTrackingPath.Perspective, eventId);
    coordinator.ExpectPerspectiveCompletions(eventId, [PERSPECTIVE_A, PERSPECTIVE_B]);
    coordinator.SignalPerspectiveComplete(eventId, PERSPECTIVE_A);

    var cleaned = coordinator.CleanupStaleTracking(_bound);

    await Assert.That(cleaned).IsEqualTo(1).Because("only the dispatch path's entry went quiet past the bound");
    await Assert.That(coordinator.GetTracking(LifecycleTrackingPath.LocalDispatch, eventId)).IsNull();
    await Assert.That(coordinator.GetTracking(LifecycleTrackingPath.Perspective, eventId)).IsNotNull();
    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsFalse()
      .Because("the sweep ending another path's entry never drops the projection's gate");
  }

  [Test]
  public async Task SegmentLatch_FiresPostLifecycleOnItsOwnPathsTracking_AndEndsWithItAsync(CancellationToken cancellationToken) {
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance);
    Guid eventId = TrackedGuid.New();
    var dispatch = _begin(coordinator, LifecycleTrackingPath.LocalDispatch, eventId);
    var projection = _begin(coordinator, LifecycleTrackingPath.Perspective, eventId);
    coordinator.ExpectCompletionsFrom(LifecycleTrackingPath.LocalDispatch, eventId, PostLifecycleCompletionSource.Local);
    await using var provider = _provider();

    await coordinator.SignalSegmentCompleteAsync(
      LifecycleTrackingPath.LocalDispatch, eventId, PostLifecycleCompletionSource.Local, provider, cancellationToken);

    await coordinator.DrainAllDetachedAsync();
    await Assert.That(dispatch.IsComplete).IsTrue();
    await Assert.That(projection.IsComplete).IsFalse()
      .Because("the latch fires PostLifecycle on the tracking of the path it belongs to, and no other");
  }
}
