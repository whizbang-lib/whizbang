// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
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
/// Every lifecycle tracking entry has a defined end (#1797). The stale sweep is a safety net on the
/// injected clock: it never keeps a completed entry, and it ends a partial entry whose perspectives
/// stopped reporting, counted and logged with the perspectives that never reported.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleCoordinator.cs</code-under-test>
[Category("Core")]
[Category("Lifecycle")]
public class LifecycleTrackingEndsTests {
  private const string PARTIAL_ENDED = "whizbang.lifecycle_coordinator.partial_tracking_ended";
  private const string COMPLETED_SWEPT = "whizbang.lifecycle_coordinator.completed_tracking_swept";
  private const string TRACKED_ENTRIES = "whizbang.lifecycle_coordinator.tracked_entries";
  private const string PENDING_PERSPECTIVES = "whizbang.lifecycle_coordinator.pending_perspective_states";
  private const string PENDING_WHEN_ALL = "whizbang.lifecycle_coordinator.pending_when_all_states";
  private const string PERSPECTIVE_A = "Test.APerspective";
  private const string PERSPECTIVE_B = "Test.BPerspective";
  private const string PERSPECTIVE_C = "Test.CPerspective";

  private static readonly TimeSpan _bound = TimeSpan.FromMinutes(5);

  private sealed record EndsEvent(Guid Id) : IEvent;

  private sealed class CompletingInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(
        IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private static MessageEnvelope<EndsEvent> _envelope(Guid id) => new() {
    MessageId = MessageId.From(id),
    Payload = new EndsEvent(id),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private static Guid _begin(LifecycleCoordinator coordinator) {
    Guid eventId = TrackedGuid.New();
    coordinator.BeginTracking(eventId, _envelope(eventId), LifecycleStage.PrePerspectiveDetached, MessageSource.Local);
    return eventId;
  }

  private static double _current(MetricAssertionHelper helper, string name) =>
    helper.GetByName(name).Where(m => m.Tags.Count == 0).Select(m => m.Value).LastOrDefault();

  [Test]
  [NotInParallel("Metrics")]
  public async Task Sweep_EndsAPartialEntry_OnceNoPerspectiveHasReportedForTheBound_NamingTheMissingPerspectivesAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new LifecycleCoordinatorMetrics(new WhizbangMetrics(factory));
    var clock = new FakeTimeProvider();
    var logger = new FakeLogger<LifecycleCoordinator>();
    var coordinator = new LifecycleCoordinator(logger, metrics, clock);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var eventId = _begin(coordinator);
    coordinator.ExpectPerspectiveCompletions(eventId, [PERSPECTIVE_A, PERSPECTIVE_B, PERSPECTIVE_C]);
    coordinator.SignalPerspectiveComplete(eventId, PERSPECTIVE_A);

    clock.Advance(_bound + TimeSpan.FromSeconds(1));
    var cleaned = coordinator.CleanupStaleTracking(_bound);

    await Assert.That(cleaned).IsEqualTo(1)
      .Because("a partial entry no perspective has reported for within the bound can never be relied on to complete");
    await Assert.That(coordinator.GetTracking(eventId)).IsNull();
    await Assert.That(_current(helper, PARTIAL_ENDED)).IsEqualTo(1)
      .Because("ending a partial entry means PostAllPerspectives will not fire for it, so it is counted");
    var record = logger.Collector.GetSnapshot().Single(r => r.Message.Contains(eventId.ToString(), StringComparison.Ordinal));
    await Assert.That(record.Level).IsEqualTo(LogLevel.Debug);
    await Assert.That(record.Message).Contains(PERSPECTIVE_B)
      .Because("the log names every perspective that never reported");
    await Assert.That(record.Message).Contains(PERSPECTIVE_C);
    await Assert.That(record.Message.Contains(PERSPECTIVE_A, StringComparison.Ordinal)).IsFalse()
      .Because("a perspective that did report is not among the missing");
  }

  [Test]
  public async Task Sweep_KeepsAPartialEntry_WhilePerspectivesKeepReportingWithinTheBoundAsync() {
    var clock = new FakeTimeProvider();
    var coordinator = new LifecycleCoordinator(new FakeLogger<LifecycleCoordinator>(), timeProvider: clock);
    var eventId = _begin(coordinator);
    coordinator.ExpectPerspectiveCompletions(eventId, [PERSPECTIVE_A, PERSPECTIVE_B, PERSPECTIVE_C]);

    clock.Advance(TimeSpan.FromMinutes(4));
    coordinator.SignalPerspectiveComplete(eventId, PERSPECTIVE_A);
    clock.Advance(TimeSpan.FromMinutes(4));
    var cleaned = coordinator.CleanupStaleTracking(_bound);

    await Assert.That(cleaned).IsEqualTo(0)
      .Because("each report restarts the bound, so an entry its perspectives are still reporting for is never ended early");
    await Assert.That(coordinator.GetTracking(eventId)).IsNotNull();
    coordinator.SignalPerspectiveComplete(eventId, PERSPECTIVE_B);
    await Assert.That(coordinator.SignalPerspectiveComplete(eventId, PERSPECTIVE_C)).IsTrue()
      .Because("the kept entry still completes its WhenAll gate when the last perspective reports");
  }

  [Test]
  public async Task Sweep_KeepsAnUnreportedEntry_UntilTheInjectedClockPassesTheBoundAsync() {
    var clock = new FakeTimeProvider();
    var coordinator = new LifecycleCoordinator(new FakeLogger<LifecycleCoordinator>(), timeProvider: clock);
    var eventId = _begin(coordinator);

    clock.Advance(_bound - TimeSpan.FromSeconds(1));
    await Assert.That(coordinator.CleanupStaleTracking(_bound)).IsEqualTo(0)
      .Because("the bound is read on the injected clock, which has not passed it yet");
    clock.Advance(TimeSpan.FromSeconds(2));
    await Assert.That(coordinator.CleanupStaleTracking(_bound)).IsEqualTo(1);
    await Assert.That(coordinator.GetTracking(eventId)).IsNull();
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task Sweep_NeverRetainsACompletedEntry_HoweverRecentAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new LifecycleCoordinatorMetrics(new WhizbangMetrics(factory));
    var clock = new FakeTimeProvider();
    var coordinator = new LifecycleCoordinator(new FakeLogger<LifecycleCoordinator>(), metrics, clock);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var eventId = _begin(coordinator);
    await using var provider = new ServiceCollection()
      .AddSingleton<IReceptorInvoker, CompletingInvoker>()
      .BuildServiceProvider();
    await coordinator.GetTracking(eventId)!.AdvanceToAsync(LifecycleStage.PostLifecycleInline, provider, CancellationToken.None);

    var cleaned = coordinator.CleanupStaleTracking(TimeSpan.FromHours(1));

    await Assert.That(cleaned).IsEqualTo(1)
      .Because("a completed entry has nothing left to wait for; the sweep never keeps one");
    await Assert.That(coordinator.GetTracking(eventId)).IsNull();
    await Assert.That(_current(helper, COMPLETED_SWEPT)).IsEqualTo(1)
      .Because("a completed entry reaching the sweep means a path skipped its exit, which is counted");
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task TrackedEntriesGauge_ReportsTheLiveTrackedCountAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new LifecycleCoordinatorMetrics(new WhizbangMetrics(factory));
    var coordinator = new LifecycleCoordinator(new FakeLogger<LifecycleCoordinator>(), metrics, new FakeTimeProvider());
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var first = _begin(coordinator);
    _begin(coordinator);
    _begin(coordinator);

    coordinator.AbandonTracking(first);

    await Assert.That(_current(helper, TRACKED_ENTRIES)).IsEqualTo(2)
      .Because("the gauge reads the entries the coordinator holds right now, so a leak shows on a dashboard");
  }
  [Test]
  [NotInParallel("Metrics")]
  public async Task EndingACompletedEvent_TakesItsPerspectiveStateOffThePendingGaugeOnlyOnceAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new LifecycleCoordinatorMetrics(new WhizbangMetrics(factory));
    var coordinator = new LifecycleCoordinator(new FakeLogger<LifecycleCoordinator>(), metrics, new FakeTimeProvider());
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var eventId = _begin(coordinator);
    coordinator.ExpectPerspectiveCompletions(eventId, [PERSPECTIVE_A]);
    coordinator.SignalPerspectiveComplete(eventId, PERSPECTIVE_A);

    coordinator.AbandonTracking(eventId);

    await Assert.That(_current(helper, PENDING_PERSPECTIVES)).IsEqualTo(0)
      .Because("the state left the pending gauge when it completed; ending the event must not take it off again");
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task Sweep_EndingAnEntry_ReleasesItsSegmentAndPerspectiveGatesAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new LifecycleCoordinatorMetrics(new WhizbangMetrics(factory));
    var clock = new FakeTimeProvider();
    var coordinator = new LifecycleCoordinator(new FakeLogger<LifecycleCoordinator>(), metrics, clock);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var eventId = _begin(coordinator);
    coordinator.ExpectCompletionsFrom(eventId, PostLifecycleCompletionSource.Local);
    coordinator.ExpectPerspectiveCompletions(eventId, [PERSPECTIVE_A, PERSPECTIVE_B]);

    clock.Advance(_bound + TimeSpan.FromSeconds(1));
    coordinator.CleanupStaleTracking(_bound);

    await Assert.That(_current(helper, PENDING_WHEN_ALL)).IsEqualTo(0)
      .Because("an ended entry's segment gate goes with it");
    await Assert.That(_current(helper, PENDING_PERSPECTIVES)).IsEqualTo(0)
      .Because("an ended entry's incomplete perspective gate goes with it");
  }

  [Test]
  public async Task Sweep_SkipsAnEntryAnotherPathEndedWhileItRan_CountingItOnceAsync() {
    var clock = new FakeTimeProvider();
    var logger = new EndingOthersLogger();
    var coordinator = new LifecycleCoordinator(logger, timeProvider: clock);
    logger.Coordinator = coordinator;
    var first = _begin(coordinator);
    var second = _begin(coordinator);
    logger.Others = [first, second];
    foreach (var id in logger.Others) {
      coordinator.ExpectPerspectiveCompletions(id, [PERSPECTIVE_A, PERSPECTIVE_B]);
      coordinator.SignalPerspectiveComplete(id, PERSPECTIVE_A);
    }
    clock.Advance(_bound + TimeSpan.FromSeconds(1));

    // Ending the first partial entry logs, and the log ends the other entry as a worker's exit would mid-sweep.
    var cleaned = coordinator.CleanupStaleTracking(_bound);

    await Assert.That(cleaned).IsEqualTo(1)
      .Because("the entry another path ended first is not counted again by the sweep");
    await Assert.That(coordinator.TrackedCount).IsEqualTo(0);
  }

  /// <summary>Ends every other tracked event the first time the coordinator logs, as a concurrent exit would.</summary>
  private sealed class EndingOthersLogger : ILogger<LifecycleCoordinator> {
    public LifecycleCoordinator? Coordinator { get; set; }
    public Guid[] Others { get; set; } = [];
    private bool _fired;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (_fired) {
        return;
      }
      _fired = true;
      var message = formatter(state, exception);
      foreach (var other in Others.Where(id => !message.Contains(id.ToString(), StringComparison.Ordinal))) {
        Coordinator!.AbandonTracking(other);
      }
    }
  }
}
