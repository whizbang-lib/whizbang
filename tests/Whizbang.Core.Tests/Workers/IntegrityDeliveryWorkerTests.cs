// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Startup;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Core.Tests.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The integrity delivery drain (#2001): on the maintainer only, under the housekeeping idle gate, it handles queued
/// integrity deliveries in arrival order, one at a time, up to a per-pass bound; a failure is retried after an
/// exponential backoff, and a delivery that has spent its attempts, or whose type no handler is registered for, is
/// dropped with a warning naming its type.
/// </summary>
/// <remarks>
/// Each pass is driven directly through <c>RunPassAsync</c> on a fake clock; nothing here waits.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityDeliveryWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityAdmission.cs</code-under-test>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
[Category("Workers")]
public class IntegrityDeliveryWorkerTests {
  private const string TYPE = "test-delivery";
  private static readonly DateTimeOffset _origin = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

  private sealed class Fixture(
      IntegrityDeliveryWorker worker, InMemoryIntegrityDeliveryCoordinator coordinator, IntegrityDeliveryRegistry deliveries,
      HousekeepingCoordinator? housekeeping, FakeTimeProvider clock, CapturingLogger logger, ServiceProvider services)
      : IAsyncDisposable {
    public IntegrityDeliveryWorker Worker { get; } = worker;
    public InMemoryIntegrityDeliveryCoordinator Coordinator { get; } = coordinator;
    public IntegrityDeliveryRegistry Deliveries { get; } = deliveries;
    public HousekeepingCoordinator? Housekeeping { get; } = housekeeping;
    public FakeTimeProvider Clock { get; } = clock;
    public CapturingLogger Logger { get; } = logger;
    public List<(string Payload, bool InsideTheDrain)> Handled { get; } = [];
    public Func<string, Exception?> Fail { get; set; } = _ => null;
    public ValueTask DisposeAsync() => services.DisposeAsync();
  }

  private static async Task<Fixture> _buildAsync(
      bool? maintainer = true, bool withCoordinator = true, bool withHousekeeping = true,
      Action<StreamIntegrityOptions>? configure = null, Whizbang.Core.Observability.StreamIntegrityMetrics? metrics = null) {
    var clock = new FakeTimeProvider(_origin);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator();
    var deliveries = new IntegrityDeliveryRegistry();
    var housekeeping = withHousekeeping
      ? new HousekeepingCoordinator(new HousekeepingCoordinator.Settings { SettledCooldown = TimeSpan.Zero, MaxDeferral = TimeSpan.FromHours(1) }, clock)
      : null;
    var services = new ServiceCollection();
    if (withCoordinator) {
      services.AddScoped<IWorkCoordinator>(_ => coordinator);
    }
    if (housekeeping is not null) {
      services.AddSingleton(housekeeping);
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    if (maintainer is { } holds) {
      services.AddSingleton(await MaintainerDutyHolders.ElectedAsync(grants: holds));
    }
    var provider = services.BuildServiceProvider();
    var options = new StreamIntegrityOptions();
    configure?.Invoke(options);
    var logger = new CapturingLogger();
    var worker = new IntegrityDeliveryWorker(
      provider.GetRequiredService<IServiceScopeFactory>(), new SchemaReadyGate(), Options.Create(options), deliveries,
      logger, clock);
    var fixture = new Fixture(worker, coordinator, deliveries, housekeeping, clock, logger, provider);
    deliveries.Register(TYPE, (payload, _, _) => {
      fixture.Handled.Add((payload, IntegrityDrainContext.IsActive));
      return fixture.Fail(payload) is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    });
    return fixture;
  }

  private static Guid _id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

  [Test]
  public async Task OnAnInstanceThatDoesNotHoldTheMaintainerDuty_ThePassDoesNothingAsync() {
    await using var fx = await _buildAsync(maintainer: false);
    fx.Coordinator.Seed(_id(1), TYPE, "1");

    var pass = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(pass.Outcome).IsEqualTo(IntegrityDeliveryWorker.PassOutcome.NotThisInstancesTurn);
    await Assert.That(fx.Coordinator.LeaseSizes).IsEmpty()
      .Because("with the maintainer duty assigned, only its holder drains, so N instances do not do the work N times");
  }

  [Test]
  public async Task WithoutRoleAssignment_EveryInstanceDrainsAsync() {
    await using var fx = await _buildAsync(maintainer: null);
    fx.Coordinator.Seed(_id(1), TYPE, "1");

    var pass = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(pass.Outcome).IsEqualTo(IntegrityDeliveryWorker.PassOutcome.Drained);
    await Assert.That(pass.Processed).IsEqualTo(1);
  }

  [Test]
  public async Task WithoutACoordinator_ThePassDoesNothingAsync() {
    await using var fx = await _buildAsync(withCoordinator: false);

    var pass = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(pass.Outcome).IsEqualTo(IntegrityDeliveryWorker.PassOutcome.NoCoordinator);
  }

  [Test]
  public async Task TheDrain_HandlesDeliveriesInArrivalOrder_InsideTheDrain_AndCompletesEachAsync() {
    await using var fx = await _buildAsync();
    fx.Coordinator.Seed(_id(3), TYPE, "first");
    fx.Coordinator.Seed(_id(1), TYPE, "second");
    fx.Coordinator.Seed(_id(2), TYPE, "third");

    var pass = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(fx.Handled.ConvertAll(h => h.Payload)).IsEquivalentTo(["first", "second", "third"]);
    await Assert.That(fx.Handled[0].Payload).IsEqualTo("first");
    await Assert.That(fx.Handled[2].Payload).IsEqualTo("third");
    await Assert.That(fx.Handled.All(h => h.InsideTheDrain)).IsTrue()
      .Because("a receptor the drain runs must run for real, not record itself again");
    await Assert.That(fx.Coordinator.Completed).IsEquivalentTo([_id(3), _id(1), _id(2)]);
    await Assert.That(pass.Processed).IsEqualTo(3);
    await Assert.That(IntegrityDrainContext.IsActive).IsFalse().Because("the drain context ends with each delivery");
  }

  [Test]
  public async Task OnABusyService_ThePassIsDeferred_AndTheSlotIsNotHeldAsync() {
    await using var fx = await _buildAsync();
    fx.Coordinator.Backlog = new ServiceBacklog { UnprocessedInboxRows = 500, ActiveLeasedRows = 3 };
    fx.Coordinator.Seed(_id(1), TYPE, "1");

    var pass = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(pass.Outcome).IsEqualTo(IntegrityDeliveryWorker.PassOutcome.Deferred);
    await Assert.That(fx.Coordinator.LeaseSizes).IsEmpty();
    await Assert.That(fx.Worker.DeferredSince).IsEqualTo(_origin);
    var sweep = fx.Housekeeping!.TryBegin(HousekeepingCoordinator.Activity.Maintenance, new ServiceBacklog());
    await Assert.That(sweep.Granted).IsTrue().Because("a refused pass holds nothing");
  }

  [Test]
  public async Task AfterMaxDeferral_ThePassIsForcedThrough_AndDeferredSinceClearsAsync() {
    await using var fx = await _buildAsync();
    fx.Coordinator.Backlog = new ServiceBacklog { UnprocessedInboxRows = 500 };
    fx.Coordinator.Seed(_id(1), TYPE, "1");
    _ = await fx.Worker.RunPassAsync(CancellationToken.None);
    fx.Clock.Advance(TimeSpan.FromMinutes(30));
    _ = await fx.Worker.RunPassAsync(CancellationToken.None);
    var stillDeferredSince = fx.Worker.DeferredSince;
    fx.Clock.Advance(TimeSpan.FromMinutes(30));

    var forced = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(stillDeferredSince).IsEqualTo(_origin).Because("deferred-since marks the first deferral of the run");
    await Assert.That(forced.Outcome).IsEqualTo(IntegrityDeliveryWorker.PassOutcome.Drained);
    await Assert.That(forced.Processed).IsEqualTo(1);
    await Assert.That(fx.Worker.DeferredSince).IsNull();
  }

  [Test]
  public async Task AnAdmittedPass_ReleasesTheSlotWhenItEndsAsync() {
    await using var fx = await _buildAsync();
    fx.Coordinator.Seed(_id(1), TYPE, "1");

    _ = await fx.Worker.RunPassAsync(CancellationToken.None);

    var next = fx.Housekeeping!.TryBegin(HousekeepingCoordinator.Activity.Integrity, new ServiceBacklog());
    await Assert.That(next.Granted).IsTrue().Because("a leaked integrity slot would disable the sweep for the life of the process");
  }

  [Test]
  public async Task AnUnmeasuredService_DrainsAsBefore_AndTheFailureIsLoggedAsync() {
    await using var fx = await _buildAsync();
    fx.Coordinator.BacklogFailure = new InvalidOperationException("probe down");
    fx.Coordinator.Seed(_id(1), TYPE, "1");

    var pass = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(pass.Processed).IsEqualTo(1).Because("a gate that cannot measure must never disable what it gates");
    await Assert.That(fx.Logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Exception?.Message == "probe down")).IsTrue();
  }

  [Test]
  public async Task WithoutTheHousekeepingCoordinator_DrainsUngatedAsync() {
    await using var fx = await _buildAsync(withHousekeeping: false);
    fx.Coordinator.Seed(_id(1), TYPE, "1");

    var pass = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(pass.Processed).IsEqualTo(1);
  }

  [Test]
  public async Task AFailedDelivery_IsRetriedAfterAnExponentialBackoffAsync() {
    await using var fx = await _buildAsync(configure: o => o.IntegrityDeliveryRetryBackoffSeconds = 10);
    fx.Coordinator.Seed(_id(1), TYPE, "fresh");
    fx.Coordinator.Seed(_id(2), TYPE, "failed-once", attempts: 1);
    fx.Fail = _ => new InvalidOperationException("handler broke");

    var pass = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(pass.Failed).IsEqualTo(2);
    await Assert.That(fx.Coordinator.Completed).IsEmpty();
    await Assert.That(fx.Coordinator.Failed[0]).IsEqualTo((_id(1), "handler broke", TimeSpan.FromSeconds(10)));
    await Assert.That(fx.Coordinator.Failed[1]).IsEqualTo((_id(2), "handler broke", TimeSpan.FromSeconds(20)))
      .Because("each attempt doubles the wait");
  }

  [Test]
  public async Task TheBackoff_IsCappedAtAnHourAsync() {
    await using var fx = await _buildAsync(configure: o => {
      o.IntegrityDeliveryRetryBackoffSeconds = 1000;
      o.MaxIntegrityDeliveryAttempts = 100;
    });
    fx.Coordinator.Seed(_id(1), TYPE, "x", attempts: 10);
    fx.Fail = _ => new InvalidOperationException("still broken");

    _ = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(fx.Coordinator.Failed.Single().Backoff).IsEqualTo(TimeSpan.FromHours(1));
  }

  [Test]
  public async Task ADeliveryThatHasSpentItsAttempts_IsDropped_WithAWarningNamingItsTypeAndErrorAsync() {
    await using var fx = await _buildAsync(configure: o => o.MaxIntegrityDeliveryAttempts = 3);
    fx.Coordinator.Seed(_id(1), TYPE, "doomed", attempts: 2);
    fx.Fail = _ => new InvalidOperationException("permanently broken");

    var pass = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(pass.Dropped).IsEqualTo(1);
    await Assert.That(fx.Coordinator.Failed).IsEmpty();
    await Assert.That(fx.Coordinator.Completed).IsEquivalentTo([_id(1)])
      .Because("control-plane traffic is dropped at its bound, not dead-lettered");
    var warning = fx.Logger.Entries.Single(e => e.Level == LogLevel.Warning);
    await Assert.That(warning.Message).Contains(TYPE);
    await Assert.That(warning.Message).Contains("permanently broken");
  }

  [Test]
  public async Task ADeliveryNoHandlerIsRegisteredFor_IsDropped_WithAWarningAsync() {
    await using var fx = await _buildAsync();
    fx.Coordinator.Seed(_id(1), "a-type-from-another-version", "{}");

    var pass = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(pass.Dropped).IsEqualTo(1);
    await Assert.That(fx.Coordinator.Completed).IsEquivalentTo([_id(1)]);
    await Assert.That(fx.Logger.Entries.Single(e => e.Level == LogLevel.Warning).Message).Contains("a-type-from-another-version");
  }

  [Test]
  public async Task APass_IsBoundedByMaxDeliveriesPerPass_LeasedInBatchesAsync() {
    await using var fx = await _buildAsync(configure: o => {
      o.IntegrityDeliveryBatchSize = 2;
      o.MaxIntegrityDeliveriesPerPass = 5;
    });
    for (var i = 1; i <= 10; i++) {
      fx.Coordinator.Seed(_id(i), TYPE, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    var pass = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(fx.Coordinator.LeaseSizes).IsEquivalentTo([2, 2, 1]);
    await Assert.That(pass.Processed).IsEqualTo(5);
    await Assert.That(fx.Coordinator.Pending.Count).IsEqualTo(5).Because("the rest waits for the next pass");
  }

  [Test]
  public async Task APass_StopsWhenALeaseComesBackShortAsync() {
    await using var fx = await _buildAsync(configure: o => o.IntegrityDeliveryBatchSize = 4);
    fx.Coordinator.Seed(_id(1), TYPE, "1");

    _ = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(fx.Coordinator.LeaseSizes).IsEquivalentTo([4])
      .Because("a short lease means the queue is empty; asking again would be a wasted round trip");
  }

  [Test]
  public async Task Cancellation_StopsThePass_WithoutCountingAFailureAsync() {
    await using var fx = await _buildAsync();
    fx.Coordinator.Seed(_id(1), TYPE, "1");
    using var cts = new CancellationTokenSource();
    fx.Fail = _ => {
      cts.Cancel();
      return new OperationCanceledException(cts.Token);
    };

    await Assert.That(async () => await fx.Worker.RunPassAsync(cts.Token)).Throws<OperationCanceledException>();
    await Assert.That(fx.Coordinator.Failed).IsEmpty();
  }

  [Test]
  public async Task AHandlerThatTimesOutOnItsOwn_CountsAsAFailedAttemptAsync() {
    await using var fx = await _buildAsync();
    fx.Coordinator.Seed(_id(1), TYPE, "1");
    fx.Fail = _ => new OperationCanceledException("a timeout inside the handler");

    var pass = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(pass.Failed).IsEqualTo(1);
  }

  [Test]
  public async Task EveryOutcome_IsCountedAsync() {
    using var probe = new IntegrityMetricsProbe();
    await using var fx = await _buildAsync(configure: o => o.MaxIntegrityDeliveryAttempts = 2, metrics: probe.Metrics);
    fx.Coordinator.Seed(_id(1), TYPE, "ok");
    fx.Coordinator.Seed(_id(2), TYPE, "fails");
    fx.Coordinator.Seed(_id(3), TYPE, "exhausted", attempts: 1);
    fx.Coordinator.Seed(_id(4), "unknown-type", "{}");
    fx.Fail = payload => payload == "ok" ? null : new InvalidOperationException("no");

    _ = await fx.Worker.RunPassAsync(CancellationToken.None);

    await Assert.That(probe.Total(probe.Metrics.IntegrityDeliveriesProcessed)).IsEqualTo(1);
    await Assert.That(probe.Total(probe.Metrics.IntegrityDeliveriesFailed)).IsEqualTo(1);
    await Assert.That(probe.Total(probe.Metrics.IntegrityDeliveriesDropped)).IsEqualTo(2)
      .Because("one spent its attempts and one has no handler");
  }

  internal sealed class CapturingLogger : ILogger<IntegrityDeliveryWorker> {
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception), exception));
  }
}
