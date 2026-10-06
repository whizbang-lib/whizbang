// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Branch coverage for <see cref="LifecycleCoordinator"/>: the metrics-registered side of every
/// <c>_metrics?.</c> gauge/counter update on the WhenAll, abandon and perspective paths, and the
/// "already fully complete" short-circuit of the partial-completions guard used by stale cleanup.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleCoordinator.cs</code-under-test>
[Category("Core")]
[Category("Lifecycle")]
public class LifecycleCoordinatorBranchCoverageTests {
  private const string PENDING_WHEN_ALL = "whizbang.lifecycle_coordinator.pending_when_all_states";
  private const string PENDING_PERSPECTIVES = "whizbang.lifecycle_coordinator.pending_perspective_states";
  private const string POST_LIFECYCLE_FIRED = "whizbang.lifecycle_coordinator.post_lifecycle_fired";
  private const string ALL_PERSPECTIVES_COMPLETED = "whizbang.lifecycle_coordinator.all_perspectives_completed";
  private const string EXPECTATIONS_NOT_REGISTERED = "whizbang.lifecycle_coordinator.expectations_not_registered";

  private sealed record BranchEvent(Guid Id) : IEvent;

  private static MessageEnvelope<BranchEvent> _createEnvelope(Guid id) {
    return new MessageEnvelope<BranchEvent> {
      MessageId = MessageId.From(TrackedGuid.New()),
      Payload = new BranchEvent(id),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  /// <summary>The untagged (current cumulative) value a passive counter reports on collection.</summary>
  private static double _current(MetricAssertionHelper helper, string name) {
    var measurements = helper.GetByName(name);
    return measurements.First(m => m.Tags.Count == 0).Value;
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task SegmentFanIn_WithMetrics_PendingGaugeRisesThenFallsAndPostLifecycleCountsOnceAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new LifecycleCoordinatorMetrics(new WhizbangMetrics(factory));
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance, metrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    await using var provider = new ServiceCollection().BuildServiceProvider();
    Guid eventId = TrackedGuid.New();
    coordinator.BeginTracking(eventId, _createEnvelope(eventId), LifecycleStage.PrePerspectiveDetached, MessageSource.Local);

    coordinator.ExpectCompletionsFrom(eventId, PostLifecycleCompletionSource.Local, PostLifecycleCompletionSource.Outbox);
    // A second registration is ignored (TryAdd fails), so the gauge must not double-count.
    coordinator.ExpectCompletionsFrom(eventId, PostLifecycleCompletionSource.Local, PostLifecycleCompletionSource.Outbox);
    await Assert.That(_current(helper, PENDING_WHEN_ALL)).IsEqualTo(1)
      .Because("exactly one WhenAll latch is open for the event");

    await coordinator.SignalSegmentCompleteAsync(eventId, PostLifecycleCompletionSource.Local, provider, CancellationToken.None);
    await Assert.That(_current(helper, PENDING_WHEN_ALL)).IsEqualTo(1)
      .Because("one of two segments is still outstanding, so the latch stays open");
    await Assert.That(_current(helper, POST_LIFECYCLE_FIRED)).IsEqualTo(0);

    await coordinator.SignalSegmentCompleteAsync(eventId, PostLifecycleCompletionSource.Outbox, provider, CancellationToken.None);
    await Assert.That(_current(helper, PENDING_WHEN_ALL)).IsEqualTo(0)
      .Because("the last segment closed the latch, so the pending gauge returns to zero");
    await Assert.That(_current(helper, POST_LIFECYCLE_FIRED)).IsEqualTo(1)
      .Because("a tracked event's PostLifecycle fired exactly once when the fan-in completed");
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task AbandonTracking_WithMetrics_ClosesBothPendingGaugesAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new LifecycleCoordinatorMetrics(new WhizbangMetrics(factory));
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance, metrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    Guid eventId = TrackedGuid.New();
    coordinator.ExpectCompletionsFrom(eventId, PostLifecycleCompletionSource.Local);
    coordinator.ExpectPerspectiveCompletions(eventId, ["A", "B"]);
    await Assert.That(_current(helper, PENDING_WHEN_ALL)).IsEqualTo(1);
    await Assert.That(_current(helper, PENDING_PERSPECTIVES)).IsEqualTo(1);

    coordinator.AbandonTracking(eventId);

    await Assert.That(_current(helper, PENDING_WHEN_ALL)).IsEqualTo(0)
      .Because("abandoning removed the WhenAll latch, so its gauge must not leak a pending state");
    await Assert.That(_current(helper, PENDING_PERSPECTIVES)).IsEqualTo(0)
      .Because("abandoning removed the perspective expectations, so their gauge must not leak either");
    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsTrue()
      .Because("with the expectations gone there is no WhenAll gate left");
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task SignalPerspectiveComplete_WithMetrics_LastSignalCountsCompletionAndClosesGaugeAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new LifecycleCoordinatorMetrics(new WhizbangMetrics(factory));
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance, metrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    Guid eventId = TrackedGuid.New();
    coordinator.ExpectPerspectiveCompletions(eventId, ["A", "B"]);

    var first = coordinator.SignalPerspectiveComplete(eventId, "A");
    await Assert.That(first).IsFalse();
    await Assert.That(_current(helper, ALL_PERSPECTIVES_COMPLETED)).IsEqualTo(0);
    await Assert.That(_current(helper, PENDING_PERSPECTIVES)).IsEqualTo(1);

    var second = coordinator.SignalPerspectiveComplete(eventId, "B");

    await Assert.That(second).IsTrue();
    await Assert.That(_current(helper, ALL_PERSPECTIVES_COMPLETED)).IsEqualTo(1)
      .Because("the set completed once, on the last expected signal");
    await Assert.That(_current(helper, PENDING_PERSPECTIVES)).IsEqualTo(0)
      .Because("a completed perspective set is no longer pending");
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task AreAllPerspectivesComplete_WithMetricsAndNoExpectations_CountsAndReturnsTrueAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new LifecycleCoordinatorMetrics(new WhizbangMetrics(factory));
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance, metrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    var result = coordinator.AreAllPerspectivesComplete(TrackedGuid.New());

    await Assert.That(result).IsTrue()
      .Because("no registered expectations means no gate: the terminal stages must still fire");
    await Assert.That(_current(helper, EXPECTATIONS_NOT_REGISTERED)).IsEqualTo(1)
      .Because("the ungated answer is counted so an operator can see events that skipped the gate");
  }

  [Test]
  public async Task CleanupStaleTracking_PerspectivesAllCompleteButLifecycleNot_RemovesEntryAsync() {
    // The partial-completions guard must short-circuit on "already all complete": a set that is
    // fully complete holds no in-progress signals worth protecting, so a stale, incomplete
    // lifecycle with a COMPLETE perspective set is cleaned like any other stale entry.
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance);
    Guid eventId = TrackedGuid.New();
    coordinator.BeginTracking(eventId, _createEnvelope(eventId), LifecycleStage.PrePerspectiveDetached, MessageSource.Local);
    coordinator.ExpectPerspectiveCompletions(eventId, ["A"]);
    await Assert.That(coordinator.SignalPerspectiveComplete(eventId, "A")).IsTrue();

    var cleaned = coordinator.CleanupStaleTracking(TimeSpan.FromTicks(-1));

    await Assert.That(cleaned).IsEqualTo(1)
      .Because("a fully complete perspective set is not partial progress, so the guard must not preserve the entry");
    await Assert.That(coordinator.GetTracking(eventId)).IsNull();
  }
}
