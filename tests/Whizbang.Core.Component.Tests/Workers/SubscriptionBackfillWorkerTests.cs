// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.RunControl;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2092: the paced backfill walks each queued (owner, type) in bounded windows by the owner's commit sequence. A window
/// is requested only when the previous one has answered, at most a configured number at once, spending a token bucket
/// that refills at the configured events per minute; on the maintainer, under the idle gate, and never in report-only.
/// A window that goes unanswered is asked again, and after the allowed attempts the backfill ends as unanswered: what a
/// fleet with an owner on an older release ends in.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SubscriptionBackfillWorker.cs</code-under-test>
public class SubscriptionBackfillWorkerTests {
  private static readonly DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
  private static readonly Guid _orders = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
  private static readonly Guid _billing = Guid.Parse("0b000000-0000-0000-0000-00000000000b");
  private const string PLACED = "App.Contracts.Orders.Placed, App.Contracts";
  private const string BILLED = "App.Contracts.Billing.Billed, App.Contracts";

  private static StreamIntegrityOptions _options(int windowSize = 1_000, int perMinute = 6_000, int concurrent = 2, int attempts = 3) => new() {
    RepairMode = IntegrityRepairMode.AutoRepairCapped,
    BackfillWindowSize = windowSize,
    BackfillEventsPerMinute = perMinute,
    BackfillMaxConcurrentWindows = concurrent,
    BackfillMaxWindowAttempts = attempts,
  };

  [Test]
  public async Task Tick_RequestsTheFirstWindowOfAQueuedBackfill_FromItsOwner_AndWaitsForTheAnswerAsync() {
    var cursor = _cursor(_orders, "orders-svc", PLACED);
    var (worker, coordinator, transport) = _build(_options(), [cursor]);

    await worker.TickAsync(10, _now, CancellationToken.None);

    var (envelope, destination, _) = transport.Published.Single();
    await Assert.That(envelope.Target).IsEqualTo("orders-svc");
    await Assert.That(destination.Address).IsEqualTo("orders.requests")
      .Because("the window goes to the owner's own request address, the one its checkpoint announced");
    await Assert.That(destination.Metadata!["StreamId"].GetString()).IsEqualTo(cursor.BackfillId.ToString());
    await Assert.That(ControlMessageTtl.FromMetadata(destination.Metadata)).IsEqualTo(TimeSpan.FromMinutes(60));
    await Assert.That(envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND);
    var command = _command(envelope);
    await Assert.That(command.EventTypes).IsEquivalentTo([PLACED]);
    await Assert.That(command.BackfillId).IsEqualTo(cursor.BackfillId);
    await Assert.That(command.BackfillWindow).IsEqualTo(1L);
    await Assert.That(command.FromCommitSequence).IsNull().Because("the first window starts at the beginning of the owner's history");
    await Assert.That(command.MaxEvents).IsEqualTo(1_000).Because("a window is bounded by the configured size");
    await Assert.That(command.StateOnly).IsTrue().Because("backfilled history builds state and never re-runs reactions");
    await Assert.That(command.RequesterService).IsEqualTo("backfill-svc");
    await Assert.That(command.Topic).IsEqualTo("inbox");
    await Assert.That(coordinator.Marks).IsEquivalentTo([(cursor.BackfillId, 1L, _now, _now.AddMinutes(180))])
      .Because("the window awaits its answer for the repair answer timeout before it may be asked again");
  }

  [Test]
  public async Task Tick_AnAnsweredWindow_IsFollowedByTheNextFromWhereItStoppedAsync() {
    var cursor = _cursor(_orders, "orders-svc", PLACED) with { CursorCommitSequence = 5_400, Window = 3, WindowsRequested = 3 };
    var (worker, coordinator, transport) = _build(_options(), [cursor]);

    await worker.TickAsync(10, _now, CancellationToken.None);

    var command = _command(transport.Published.Single().Envelope);
    await Assert.That(command.FromCommitSequence).IsEqualTo(5_400L)
      .Because("#2098: the next window starts exactly where the answer said the last one stopped");
    await Assert.That(command.BackfillWindow).IsEqualTo(4L);
    await Assert.That(coordinator.Marks.Single().Window).IsEqualTo(4L);
  }

  [Test]
  public async Task Tick_AWindowAwaitingItsAnswer_IsNotAskedAgainAsync() {
    var cursor = _cursor(_orders, "orders-svc", PLACED) with { Window = 2, AnswerDueBy = _now.AddMinutes(1), WindowAttempts = 1 };
    var (worker, coordinator, transport) = _build(_options(), [cursor]);

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty()
      .Because("the next window is requested only once the previous one has answered");
    await Assert.That(coordinator.Marks).IsEmpty();
  }

  [Test]
  public async Task Tick_AWindowPastItsAnswerTimeout_IsAskedAgain_AtTheSamePositionAsync() {
    var cursor = _cursor(_orders, "orders-svc", PLACED) with {
      CursorCommitSequence = 900,
      Window = 2,
      AnswerDueBy = _now.AddSeconds(-1),
      WindowAttempts = 1,
    };
    var (worker, coordinator, transport) = _build(_options(), [cursor]);

    await worker.TickAsync(10, _now, CancellationToken.None);

    var command = _command(transport.Published.Single().Envelope);
    await Assert.That(command.BackfillWindow).IsEqualTo(2L).Because("an unanswered window is retried, not skipped");
    await Assert.That(command.FromCommitSequence).IsEqualTo(900L);
    await Assert.That(coordinator.Marks.Single().Window).IsEqualTo(2L);
  }

  [Test]
  public async Task Tick_AWindowUnansweredForEveryAttempt_EndsTheBackfillAsUnanswered_AndAsksNoMoreAsync() {
    // An owner on a release that predates windowed backfill answers the request the legacy way, with no window answer.
    // Asking forever would re-ship the same window forever; the backfill ends and the audit reports what is missing.
    var cursor = _cursor(_orders, "orders-svc", PLACED) with { Window = 1, AnswerDueBy = _now.AddSeconds(-1), WindowAttempts = 3 };
    var metrics = _metrics(out var meterServices);
    await using var ownedMeters = meterServices;
    using var ended = new MetricProbe(metrics.BackfillsEnded.Instrument, "outcome");
    var (worker, coordinator, transport) = _build(_options(attempts: 3), [cursor], metrics: metrics);

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty();
    await Assert.That(coordinator.Ended).IsEquivalentTo([(cursor.BackfillId, SubscriptionBackfillOutcome.Unanswered, _now)]);
    await Assert.That(ended.Total("unanswered")).IsEqualTo(1L);
  }

  [Test]
  public async Task Tick_AnEndedBackfill_IsLeftAloneAsync() {
    var cursor = _cursor(_orders, "orders-svc", PLACED) with { CompletedAt = _now.AddHours(-1), Outcome = SubscriptionBackfillOutcome.Complete };
    var (worker, coordinator, transport) = _build(_options(), [cursor]);

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty();
    await Assert.That(coordinator.Marks).IsEmpty();
    await Assert.That(coordinator.Ended).IsEmpty();
  }

  [Test]
  public async Task Tick_RequestsAtMostTheConfiguredWindowsAtOnceAsync() {
    var inFlight = _cursor(_billing, "billing-svc", BILLED) with { Window = 1, AnswerDueBy = _now.AddMinutes(5), WindowAttempts = 1 };
    var waiting1 = _cursor(_orders, "orders-svc", PLACED);
    var waiting2 = _cursor(_orders, "orders-svc", "App.Contracts.Orders.Shipped, App.Contracts");
    var (worker, _, transport) = _build(_options(perMinute: 1_000_000, concurrent: 2), [inFlight, waiting1, waiting2]);

    await worker.TickAsync(60, _now, CancellationToken.None);

    await Assert.That(transport.Published).Count().IsEqualTo(1)
      .Because("one window is already awaiting its answer and the limit is two, so only one more may leave");
  }

  [Test]
  public async Task Tick_TheLeastRecentlyAskedBackfillGoesFirstAsync() {
    var askedRecently = _cursor(_orders, "orders-svc", PLACED) with { Window = 4, WindowsRequested = 4, LastRequestedAt = _now.AddMinutes(-1) };
    var askedLongAgo = _cursor(_billing, "billing-svc", BILLED) with { Window = 2, WindowsRequested = 2, LastRequestedAt = _now.AddMinutes(-30) };
    var neverAsked = _cursor(_orders, "orders-svc", "App.Contracts.Orders.Shipped, App.Contracts");
    var (worker, _, transport) = _build(_options(perMinute: 1_000_000, concurrent: 2), [askedRecently, askedLongAgo, neverAsked]);

    await worker.TickAsync(60, _now, CancellationToken.None);

    var asked = transport.Published.ConvertAll(p => _command(p.Envelope).BackfillId!.Value);
    await Assert.That(asked).IsEquivalentTo([neverAsked.BackfillId, askedLongAgo.BackfillId])
      .Because("every running type progresses: the one never asked, then the one asked longest ago");
  }

  [Test]
  public async Task Tick_SpendsTheWholeWindowFromTheBucket_SoTheRateBoundsTheEventsRequestedAsync() {
    var first = _cursor(_orders, "orders-svc", PLACED);
    var second = _cursor(_billing, "billing-svc", BILLED);
    var (worker, _, transport) = _build(_options(windowSize: 1_000, perMinute: 1_000, concurrent: 5), [first, second]);

    await worker.TickAsync(30, _now, CancellationToken.None);
    await Assert.That(transport.Published).IsEmpty()
      .Because("half a minute at 1,000 events a minute buys 500 events, less than one window");

    await worker.TickAsync(30, _now, CancellationToken.None);
    await Assert.That(transport.Published).Count().IsEqualTo(1)
      .Because("a minute buys exactly one window, and the second waits for the next minute's tokens");

    await worker.TickAsync(3_600, _now, CancellationToken.None);
    await Assert.That(transport.Published).Count().IsEqualTo(2)
      .Because("an idle hour does not bank an hour of windows: the bucket holds one minute's worth, here one window");
  }

  [Test]
  public async Task Tick_AWindowLargerThanAMinutesRate_IsStillAffordableAsync() {
    var cursor = _cursor(_orders, "orders-svc", PLACED);
    var (worker, _, transport) = _build(_options(windowSize: 1_000, perMinute: 100), [cursor]);

    await worker.TickAsync(600, _now, CancellationToken.None);

    await Assert.That(transport.Published).Count().IsEqualTo(1)
      .Because("the bucket holds at least one window, or a window larger than a minute's rate could never be paid for");
  }

  [Test]
  public async Task Tick_ABackfillWithNoKnownOwner_BroadcastsItsWindow_OnTheSharedTopicAsync() {
    var cursor = _cursor(Guid.Empty, string.Empty, PLACED);
    var (worker, _, transport) = _build(_options(), [cursor], controlInbox: "inbox.whizbang.service.backfill-svc");

    await worker.TickAsync(10, _now, CancellationToken.None);

    var (envelope, destination, _) = transport.Published.Single();
    await Assert.That(envelope.Target).IsNull().Because("no owner is known, so every holder hears it and only the author answers");
    await Assert.That(destination.Address).IsEqualTo("inbox").Because("a broadcast must reach every service, which only the shared topic does");
    await Assert.That(_command(envelope).Topic).IsEqualTo("inbox.whizbang.service.backfill-svc")
      .Because("answers come back to this service's own control inbox");
  }

  [Test]
  public async Task Tick_AnOwnerWithNoRequestAddress_IsSkipped_AndTheOthersStillGoAsync() {
    var unaddressed = _cursor(_billing, "billing-svc", BILLED);
    var addressed = _cursor(_orders, "orders-svc", PLACED);
    var (worker, coordinator, transport) = _build(_options(perMinute: 1_000_000), [unaddressed, addressed],
      claims: [_claim("app.contracts.orders", _orders, "orders-svc", "orders.requests"), _claim("app.contracts.billing", _billing, "billing-svc", null)]);

    await worker.TickAsync(60, _now, CancellationToken.None);

    await Assert.That(transport.Published.Select(p => p.Envelope.Target ?? string.Empty)).IsEquivalentTo(["orders-svc"]);
    await Assert.That(coordinator.Marks.Select(m => m.BackfillId)).IsEquivalentTo([addressed.BackfillId])
      .Because("a window that could not leave is not recorded as awaiting an answer");
  }

  [Test]
  public async Task Tick_RecordsTheWindowAsRequested_BeforeItLeaves_SoAnAnswerThatOutrunsThePublishFindsItAsync() {
    // The owner may answer before the publish call returns (a fast owner, or this host's continuation delayed under
    // load). Its answer applies only to a window already recorded as awaiting it, so the record must come first.
    var cursor = _cursor(_orders, "orders-svc", PLACED);
    var (worker, coordinator, transport) = _build(_options(), [cursor]);
    var marksWhenPublished = -1;
    transport.OnPublish = () => marksWhenPublished = coordinator.Marks.Count;

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(marksWhenPublished).IsEqualTo(1)
      .Because("the window is recorded as awaiting its answer before the request can reach the owner");
    await Assert.That(coordinator.Withdrawals).IsEmpty();
  }

  [Test]
  public async Task Tick_AFailedSend_IsNotRecorded_AndTheNextBackfillStillGoesAsync() {
    var failing = _cursor(_orders, "orders-svc", PLACED) with { CursorCommitSequence = 77, Window = 2, WindowsRequested = 2, WindowAttempts = 1 };
    var sent = _cursor(_billing, "billing-svc", BILLED);
    var (worker, coordinator, transport) = _build(_options(perMinute: 1_000_000), [failing, sent]);
    transport.FailFirst = 1;

    await worker.TickAsync(60, _now, CancellationToken.None);

    await Assert.That(coordinator.Withdrawals).IsEquivalentTo([(failing, 3L)])
      .Because("the window recorded before the send is withdrawn when the send fails, back to the state it was asked from");
    await Assert.That(coordinator.Cursors.Single(c => c.BackfillId == failing.BackfillId)).IsEqualTo(failing)
      .Because("a window that could not leave is not recorded as awaiting an answer, so a later tick asks it");
    await Assert.That(coordinator.Cursors.Single(c => c.BackfillId == sent.BackfillId).Window).IsEqualTo(1L);
  }

  [Test]
  public async Task Tick_Cancellation_DuringASend_PropagatesAsync() {
    var (worker, _, transport) = _build(_options(), [_cursor(_orders, "orders-svc", PLACED)]);
    transport.FailFirst = 1;
    transport.FailWith = new OperationCanceledException();

    await Assert.That(async () => await worker.TickAsync(10, _now, CancellationToken.None)).Throws<OperationCanceledException>();
  }

  [Test]
  public async Task Tick_InReportOnly_SendsNothing_AndReadsNothingAsync() {
    var options = _options();
    options.RepairMode = IntegrityRepairMode.ReportOnly;
    var (worker, coordinator, transport) = _build(options, [_cursor(_orders, "orders-svc", PLACED)]);

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty();
    await Assert.That(coordinator.Reads).IsEqualTo(0).Because("report-only means no backfill traffic of any kind");
  }

  [Test]
  public async Task Tick_WithBackfillOnSubscriptionGrowthOff_SendsNothingAsync() {
    var options = _options();
    options.BackfillOnSubscriptionGrowth = false;
    var (worker, coordinator, transport) = _build(options, [_cursor(_orders, "orders-svc", PLACED)]);

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty();
    await Assert.That(coordinator.Reads).IsEqualTo(0);
  }

  [Test]
  public async Task Tick_OnAnInstanceWhoseRunPermitIsClosed_SendsNothingAsync() {
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    var (worker, coordinator, transport) = _build(_options(), [_cursor(_orders, "orders-svc", PLACED)], permit: permit);

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty();
    await Assert.That(coordinator.Reads).IsEqualTo(0).Because("only the instance whose turn it is does the maintainer's work");
  }

  [Test]
  public async Task Tick_WhenTheIdleGateRefusesIntegrityWork_SendsNothingAsync() {
    var housekeeping = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings { SettledCooldown = TimeSpan.Zero });
    var (worker, coordinator, transport) = _build(_options(), [_cursor(_orders, "orders-svc", PLACED)], housekeeping: housekeeping);

    using (housekeeping.BeginIntegrityScope()) {
      await worker.TickAsync(10, _now, CancellationToken.None);
    }

    await Assert.That(transport.Published).IsEmpty();
    await Assert.That(coordinator.Marks).IsEmpty();
  }

  [Test]
  public async Task Tick_WithoutATransport_SendsNothing_AndDoesNotThrowAsync() {
    var (worker, coordinator, _) = _build(_options(), [_cursor(_orders, "orders-svc", PLACED)], withTransport: false);

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(coordinator.Marks).IsEmpty().Because("the backfill stays queued until the service can send");
  }

  [Test]
  public async Task Tick_WithoutACoordinator_DoesNothingAsync() {
    var (worker, _, transport) = _build(_options(), [_cursor(_orders, "orders-svc", PLACED)], withCoordinator: false);

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task Tick_InsideAnInteractiveHandling_TheWindowStaysBackgroundAsync() {
    var (worker, _, transport) = _build(_options(), [_cursor(_orders, "orders-svc", PLACED)]);

    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      await worker.TickAsync(10, _now, CancellationToken.None);
    }

    await Assert.That(transport.Published.Single().Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("backfilled history is catch-up work an owner serves behind its live traffic");
  }

  [Test]
  public async Task Tick_CountsWindowsRequested_AndPublishesTheInFlightAndRemainingGaugesAsync() {
    var running = _cursor(_billing, "billing-svc", BILLED) with {
      Window = 2,
      AnswerDueBy = _now.AddMinutes(5),
      WindowAttempts = 1,
      FirstCommitSequence = 1,
      CursorCommitSequence = 100,
      HeadCommitSequence = 300,
      EventsReceived = 50,
    };
    var fresh = _cursor(_orders, "orders-svc", PLACED);
    var ended = _cursor(_orders, "orders-svc", "App.Contracts.Orders.Shipped, App.Contracts") with { CompletedAt = _now, Outcome = SubscriptionBackfillOutcome.Complete };
    var metrics = _metrics(out var meterServices);
    await using var ownedMeters = meterServices;
    using var requested = new MetricProbe(metrics.BackfillWindowsRequested.Instrument, "event_type");
    var (worker, _, _) = _build(_options(), [running, fresh, ended], metrics: metrics);

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(requested.Total(PLACED)).IsEqualTo(1L);
    await Assert.That(requested.Sum()).IsEqualTo(1L);
    var gauges = metrics.CurrentBackfillGaugesForTest;
    await Assert.That(gauges.WindowsInFlight).IsEqualTo(2).Because("the window already out plus the one just requested");
    await Assert.That(gauges.Remaining).IsEquivalentTo([new BackfillRemaining(BILLED, 100)])
      .Because("only a running backfill with an estimate reports; 50 events over 100 sequences leaves about 100 in the 200 ahead");
  }

  [Test]
  public async Task Tick_WithNothingQueued_SendsNothingAsync() {
    var (worker, _, transport) = _build(_options(), []);

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  [Timeout(30_000)]
  public async Task ExecuteAsync_TicksOnTheClock_AndRequestsAWindowAsync(CancellationToken testToken) {
    var clock = new FakeTimeProvider(_now);
    var (worker, coordinator, _) = _build(_options(), [_cursor(_orders, "orders-svc", PLACED)], clock: clock);

    await worker.StartAsync(testToken);
    try {
      // Ten seconds at 6,000 events a minute buys the first 1,000-event window. The loop's timer exists before StartAsync
      // returns, so each advance is a tick the loop takes, whenever it gets there.
      clock.Advance(TimeSpan.FromSeconds(10));
      await coordinator.FirstMark.Task.WaitAsync(testToken);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(coordinator.Marks.Single().Window).IsEqualTo(1L);
  }

  [Test]
  [Timeout(30_000)]
  public async Task ExecuteAsync_AFailingTick_IsLogged_AndTheLoopGoesOnAsync(CancellationToken testToken) {
    var clock = new FakeTimeProvider(_now);
    var (worker, coordinator, _) = _build(_options(), [_cursor(_orders, "orders-svc", PLACED)], clock: clock);
    coordinator.FailReadsRemaining = 1;

    await worker.StartAsync(testToken);
    try {
      clock.Advance(TimeSpan.FromSeconds(1));
      await coordinator.FirstReadFailed.Task.WaitAsync(testToken);
      clock.Advance(TimeSpan.FromSeconds(10));
      await coordinator.FirstMark.Task.WaitAsync(testToken);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse().Because("one failed tick must not stop the backfill");
  }

  [Test]
  [Timeout(30_000)]
  public async Task ExecuteAsync_CancellationInsideATick_EndsTheLoopAsync(CancellationToken testToken) {
    var clock = new FakeTimeProvider(_now);
    var (worker, coordinator, _) = _build(_options(), [_cursor(_orders, "orders-svc", PLACED)], clock: clock);
    coordinator.FailReadsRemaining = 1;
    coordinator.FailReadsWith = new OperationCanceledException();

    await worker.StartAsync(testToken);
    clock.Advance(TimeSpan.FromSeconds(1));
    await worker.ExecuteTask!.WaitAsync(testToken);

    await Assert.That(worker.ExecuteTask.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("a cancellation is the way out of the loop, never a failed tick to log and retry");
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Tick_AWindowUnansweredForEveryAttempt_OnAHostWithoutMetrics_StillEndsTheBackfillAsync() {
    var cursor = _cursor(_orders, "orders-svc", PLACED) with { Window = 1, AnswerDueBy = _now.AddSeconds(-1), WindowAttempts = 3 };
    var (worker, coordinator, _) = _build(_options(attempts: 3), [cursor]);

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(coordinator.Ended.Select(e => e.Outcome)).IsEquivalentTo([SubscriptionBackfillOutcome.Unanswered]);
  }

  [Test]
  public async Task Tick_WithoutAServiceIdentity_SendsNothingAsync() {
    var (worker, coordinator, transport) = _build(_options(), [_cursor(_orders, "orders-svc", PLACED)], withIdentity: false);

    await worker.TickAsync(10, _now, CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty().Because("an answer needs a requester to come back to");
    await Assert.That(coordinator.Marks).IsEmpty();
  }

  [Test]
  public async Task Constructor_WithoutOptions_ThrowsAsync() {
    var services = new ServiceCollection().BuildServiceProvider();

    await Assert.That(() => new SubscriptionBackfillWorker(
      services.GetRequiredService<IServiceScopeFactory>(), null!, NullLogger<SubscriptionBackfillWorker>.Instance)).Throws<ArgumentNullException>();
    await Assert.That(() => new SubscriptionBackfillWorker(
      services.GetRequiredService<IServiceScopeFactory>(), new NullValueOptions(), NullLogger<SubscriptionBackfillWorker>.Instance)).Throws<ArgumentNullException>();
  }

  [Test]
  [Timeout(30_000)]
  public async Task Dispose_AfterAStop_ReleasesTheTimer_AndIsIdempotentAsync(CancellationToken testToken) {
    var clock = new FakeTimeProvider(_now);
    var (worker, _, _) = _build(_options(), [_cursor(_orders, "orders-svc", PLACED)], clock: clock);
    await worker.StartAsync(testToken);
    await worker.StopAsync(testToken);

    worker.Dispose();

    await Assert.That(worker.Dispose).ThrowsNothing().Because("a host may dispose a worker more than once");
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsTrue();
  }

  private sealed class NullValueOptions : IOptions<StreamIntegrityOptions> {
    public StreamIntegrityOptions Value => null!;
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static SubscriptionBackfillCursor _cursor(Guid owner, string ownerName, string eventType) => new() {
    BackfillId = TrackedGuid.New().Value,
    OwnerServiceId = owner,
    OwnerServiceName = ownerName,
    EventType = eventType,
    StartedAt = _now.AddHours(-1),
  };

  private static IntegrityDomainClaim _claim(string domain, Guid owner, string name, string? requestTopic) =>
    new() { Domain = domain, OwnerServiceId = owner, OwnerServiceName = name, RequestTopic = requestTopic };

  private static StreamIntegrityMetrics _metrics(out ServiceProvider meterServices) {
    meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    return new StreamIntegrityMetrics(new WhizbangMetrics(meterServices.GetRequiredService<IMeterFactory>()));
  }

  /// <summary>
  /// Reads one passive counter's cumulative totals by one tag. The counters are observable, so each read asks the
  /// listener to observe them now; the probe is pinned to that instrument instance, since parallel tests build meters
  /// of the same name.
  /// </summary>
  private sealed class MetricProbe : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _totals = new(StringComparer.Ordinal);
    private readonly string _tagKey;

    public MetricProbe(Instrument instrument, string tagKey) {
      _tagKey = tagKey;
      _listener.InstrumentPublished = (published, l) => {
        if (ReferenceEquals(published, instrument)) {
          l.EnableMeasurementEvents(published);
        }
      };
      _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
        var tag = string.Empty;
        foreach (var kv in tags) {
          if (kv.Key == _tagKey) {
            tag = kv.Value as string ?? string.Empty;
          }
        }
        lock (_totals) {
          _totals[tag] = value;
        }
      });
      _listener.Start();
    }

    public long Total(string tag) {
      _listener.RecordObservableInstruments();
      lock (_totals) {
        return _totals.TryGetValue(tag, out var total) ? total : 0;
      }
    }

    public long Sum() {
      _listener.RecordObservableInstruments();
      lock (_totals) {
        return _totals.Values.Sum();
      }
    }

    public void Dispose() => _listener.Dispose();
  }

  private static RequestRedeliveryCommand _command(IMessageEnvelope envelope) =>
    (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(RequestRedeliveryCommand)))!;

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "A test builder: each optional collaborator is one switch a test flips, all defaulted.")]
  private static (SubscriptionBackfillWorker Worker, BackfillCoordinator Coordinator, CaptureTransport Transport) _build(
      StreamIntegrityOptions options, IReadOnlyList<SubscriptionBackfillCursor> cursors,
      IReadOnlyList<IntegrityDomainClaim>? claims = null, string? controlInbox = null,
      StreamIntegrityMetrics? metrics = null, WhizbangRunPermit? permit = null, HousekeepingCoordinator? housekeeping = null,
      bool withTransport = true, bool withCoordinator = true, TimeProvider? clock = null, bool withIdentity = true) {
    var coordinator = new BackfillCoordinator {
      Cursors = [.. cursors],
      Claims = claims ?? [
        _claim("app.contracts.orders", _orders, "orders-svc", "orders.requests"),
        _claim("app.contracts.billing", _billing, "billing-svc", "billing.requests"),
      ],
    };
    var transport = new CaptureTransport();
    var services = new ServiceCollection();
    if (withCoordinator) {
      services.AddScoped<IWorkCoordinator>(_ => coordinator);
    }
    if (withTransport) {
      services.AddSingleton<ITransport>(transport);
    }
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    if (withIdentity) {
      services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("backfill-svc"));
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    if (permit is not null) {
      services.AddSingleton(permit);
    }
    if (housekeeping is not null) {
      services.AddSingleton(housekeeping);
    }
    var consumerOptions = new TransportConsumerOptions();
    if (controlInbox is not null) {
      consumerOptions.Destinations.Add(new TransportDestination(controlInbox, Metadata: new Dictionary<string, JsonElement> {
        [Whizbang.Core.Routing.NamespaceInboxStrategy.ServiceControlInboxMetadataKey] = JsonElement.Parse("true"),
      }));
    }
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var worker = new SubscriptionBackfillWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(options),
      NullLogger<SubscriptionBackfillWorker>.Instance,
      clock);
    return (worker, coordinator, transport);
  }

  private sealed class BackfillCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<SubscriptionBackfillCursor> Cursors { get; init; } = [];
    public IReadOnlyList<IntegrityDomainClaim> Claims { get; init; } = [];
    public List<(Guid BackfillId, long Window, DateTimeOffset Now, DateTimeOffset AnswerDueBy)> Marks { get; } = [];
    public List<(Guid BackfillId, SubscriptionBackfillOutcome Outcome, DateTimeOffset Now)> Ended { get; } = [];
    public List<(SubscriptionBackfillCursor RequestedFrom, long Window)> Withdrawals { get; } = [];
    public int Reads { get; private set; }
    public int FailReadsRemaining { get; set; }
    public Exception? FailReadsWith { get; set; }
    public TaskCompletionSource FirstMark { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FirstReadFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<SubscriptionBackfillCursor>> GetSubscriptionBackfillsAsync(CancellationToken cancellationToken = default) {
      Reads++;
      if (FailReadsRemaining > 0) {
        FailReadsRemaining--;
        FirstReadFailed.TrySetResult();
        return Task.FromException<IReadOnlyList<SubscriptionBackfillCursor>>(FailReadsWith ?? new InvalidOperationException("store unavailable"));
      }
      return Task.FromResult<IReadOnlyList<SubscriptionBackfillCursor>>([.. Cursors]);
    }

    public Task<IReadOnlyList<IntegrityDomainClaim>> GetIntegrityDomainClaimsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(Claims);

    public Task MarkSubscriptionBackfillWindowRequestedAsync(
        Guid backfillId, long window, DateTimeOffset now, DateTimeOffset answerDueBy, CancellationToken cancellationToken = default) {
      Marks.Add((backfillId, window, now, answerDueBy));
      var index = Cursors.FindIndex(c => c.BackfillId == backfillId);
      Cursors[index] = Cursors[index] with { Window = window, AnswerDueBy = answerDueBy, LastRequestedAt = now };
      FirstMark.TrySetResult();
      return Task.CompletedTask;
    }

    public Task WithdrawSubscriptionBackfillWindowRequestAsync(
        SubscriptionBackfillCursor requestedFrom, long window, CancellationToken cancellationToken = default) {
      Withdrawals.Add((requestedFrom, window));
      var index = Cursors.FindIndex(c => c.BackfillId == requestedFrom.BackfillId);
      Cursors[index] = requestedFrom;
      return Task.CompletedTask;
    }

    public Task EndSubscriptionBackfillAsync(
        Guid backfillId, SubscriptionBackfillOutcome outcome, DateTimeOffset now, CancellationToken cancellationToken = default) {
      Ended.Add((backfillId, outcome, now));
      return Task.CompletedTask;
    }
  }

  private sealed class CaptureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public int FailFirst { get; set; }
    public Exception? FailWith { get; set; }
    public Action? OnPublish { get; set; }
    private int _attempts;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      OnPublish?.Invoke();
      lock (Published) {
        _attempts++;
        if (_attempts <= FailFirst) {
          throw FailWith ?? new TimeoutException("simulated broker throttle");
        }
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class InstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.Parse("0d000000-0000-0000-0000-00000000000d");
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }
}
