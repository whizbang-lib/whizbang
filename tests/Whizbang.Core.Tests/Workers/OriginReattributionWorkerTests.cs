// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Re-attribution of pre-origin history (#2008): on the maintainer, under the housekeeping idle gate, it walks the
/// store in bounded batches and hands each batch's non-owned event types to the store with their owner from the
/// ownership map, so events another service wrote leave this service's local lane for their author's lane. Types with
/// no known owner are counted as unattributed. A service with no owned domains is left exactly as it was.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/OriginReattributionWorker.cs</code-under-test>
/// <docs>resilience/stream-integrity#re-attribution</docs>
[NotInParallel(Order = 107)]
[Category("Workers")]
public class OriginReattributionWorkerTests {
  private static readonly DateTimeOffset _origin = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly ServiceBacklog _busy = new() { UnprocessedInboxRows = 500, ActiveLeasedRows = 2 };
  private static readonly Guid _billingOwner = Guid.Parse("0b000000-0000-0000-0000-0000000000b1");
  private const string OWNED_TYPE = "App.Contracts.Orders.Placed, App.Contracts";
  private const string BILLING_TYPE = "App.Contracts.Billing.Raised, App.Contracts";
  private const string UNCLAIMED_TYPE = "App.Contracts.Archive.Archived, App.Contracts";

  private sealed class WalkingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public ServiceBacklog? Backlog { get; set; } = new();
    public IReadOnlyList<IntegrityDomainClaim> Claims { get; set; } = [
      new() { Domain = "app.contracts.billing", OwnerServiceId = _billingOwner, OwnerServiceName = "billing-svc" },
    ];
    public Queue<OriginReattributionWindow?> Windows { get; } = new();
    public Func<OriginReattributionBatch, OriginReattributionResult?> Result { get; set; } =
      b => new OriginReattributionResult { Reattributed = b.Targets.Count(t => t.OwnerServiceId is not null), Advanced = true };
    public List<(string Fingerprint, int MaxEvents)> WindowReads { get; } = [];
    public List<OriginReattributionBatch> Batches { get; } = [];

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(Backlog);

    public Task<IReadOnlyList<IntegrityDomainClaim>> GetIntegrityDomainClaimsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(Claims);

    public Task<OriginReattributionWindow?> GetOriginReattributionWindowAsync(
        string ownershipFingerprint, int maxEvents, CancellationToken cancellationToken = default) {
      WindowReads.Add((ownershipFingerprint, maxEvents));
      return Task.FromResult(Windows.Count > 0 ? Windows.Dequeue() : _caughtUp(0));
    }

    public Task<OriginReattributionResult?> ReattributeOriginsAsync(
        OriginReattributionBatch batch, CancellationToken cancellationToken = default) {
      Batches.Add(batch);
      return Task.FromResult(Result(batch));
    }
  }

  private static OriginReattributionWindow _window(long after, long through, params string[] types) =>
    new() { AfterCommitSequence = after, ThroughCommitSequence = through, EventTypes = types };

  private static OriginReattributionWindow _caughtUp(long at) => _window(at, at);

  private sealed record Host(ServiceProvider Services, WalkingCoordinator Coordinator, HousekeepingCoordinator Housekeeping,
      FakeTimeProvider Clock) : IAsyncDisposable {
    public ValueTask DisposeAsync() => Services.DisposeAsync();
  }

  private static async Task<Host> _hostAsync(
      bool? maintainer = true, string[]? ownedDomains = null, StreamIntegrityMetrics? metrics = null, bool routing = true) {
    var clock = new FakeTimeProvider(_origin);
    var coordinator = new WalkingCoordinator();
    var housekeeping = new HousekeepingCoordinator(
      new HousekeepingCoordinator.Settings { SettledCooldown = TimeSpan.Zero, MaxDeferral = TimeSpan.FromHours(1) }, clock);
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton(housekeeping);
    if (routing) {
      services.AddSingleton(Options.Create(new RoutingOptions().OwnDomains(ownedDomains ?? ["App.Contracts.Orders"])));
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    if (maintainer is { } holds) {
      services.AddSingleton(await MaintainerDutyHolders.ElectedAsync(grants: holds));
    }
    return new Host(services.BuildServiceProvider(), coordinator, housekeeping, clock);
  }

  private static OriginReattributionWorker _worker(
      Host host, StreamIntegrityOptions? options = null, ILogger<OriginReattributionWorker>? logger = null) => new(
    host.Services.GetRequiredService<IServiceScopeFactory>(),
    Options.Create(options ?? new StreamIntegrityOptions { ReattributionBatchSize = 1000 }),
    logger ?? NullLogger<OriginReattributionWorker>.Instance);

  [Test]
  public async Task Pass_HandsNonOwnedTypesToTheStoreWithTheirOwner_AndUnclaimedOnesAsUnattributedAsync() {
    await using var host = await _hostAsync();
    host.Coordinator.Windows.Enqueue(_window(0, 40, OWNED_TYPE, BILLING_TYPE, UNCLAIMED_TYPE));

    var batches = await _worker(host).RunPassAsync(CancellationToken.None);

    await Assert.That(batches).IsEqualTo(1);
    var batch = host.Coordinator.Batches.Single();
    await Assert.That(batch.AfterCommitSequence).IsEqualTo(0L);
    await Assert.That(batch.ThroughCommitSequence).IsEqualTo(40L);
    await Assert.That(batch.Targets.Select(t => t.EventType)).IsEquivalentTo([BILLING_TYPE, UNCLAIMED_TYPE])
      .Because("an owned type stays in the local lane: this service wrote it");
    var billing = batch.Targets.Single(t => t.EventType == BILLING_TYPE);
    await Assert.That(billing.OwnerServiceId).IsEqualTo(_billingOwner);
    await Assert.That(billing.OwnerServiceName).IsEqualTo("billing-svc");
    var unclaimed = batch.Targets.Single(t => t.EventType == UNCLAIMED_TYPE);
    await Assert.That(unclaimed.OwnerServiceId).IsNull()
      .Because("with no known owner the rows leave the local lane only logically and are counted as unattributed");
    await Assert.That(unclaimed.OwnerServiceName).IsNull();
  }

  [Test]
  public async Task Pass_AsksTheStoreForBatchesOfTheConfiguredSize_KeyedByTheOwnershipFingerprintAsync() {
    await using var host = await _hostAsync();
    var expected = new IntegrityDomainOwnership(host.Coordinator.Claims).Fingerprint(["app.contracts.orders"]);

    await _worker(host, new StreamIntegrityOptions { ReattributionBatchSize = 250 }).RunPassAsync(CancellationToken.None);

    await Assert.That(host.Coordinator.WindowReads.Single()).IsEqualTo((expected, 250))
      .Because("a changed map or a changed set of owned domains rewalks the store, so history parked as unattributed "
             + "reaches an owner learned later");
  }

  [Test]
  public async Task Pass_RunsBatchesUntilTheWalkCatchesUpAsync() {
    await using var host = await _hostAsync();
    host.Coordinator.Windows.Enqueue(_window(0, 10, BILLING_TYPE));
    host.Coordinator.Windows.Enqueue(_window(10, 20, BILLING_TYPE));
    host.Coordinator.Windows.Enqueue(_caughtUp(20));

    var batches = await _worker(host).RunPassAsync(CancellationToken.None);

    await Assert.That(batches).IsEqualTo(2);
    await Assert.That(host.Coordinator.Batches.Select(b => b.AfterCommitSequence)).IsEquivalentTo([0L, 10L]);
  }

  [Test]
  public async Task Pass_StopsAtTheBatchesPerPassCapAsync() {
    await using var host = await _hostAsync();
    for (var i = 0; i < 5; i++) {
      host.Coordinator.Windows.Enqueue(_window(i * 10, (i + 1) * 10, BILLING_TYPE));
    }

    var batches = await _worker(host, new StreamIntegrityOptions { MaxReattributionBatchesPerPass = 3 })
      .RunPassAsync(CancellationToken.None);

    await Assert.That(batches).IsEqualTo(3).Because("a pass admitted by the idle gate, or forced through, stays bounded");
  }

  [Test]
  public async Task Pass_StopsWhenTheStoreDidNotAdvanceAsync() {
    await using var host = await _hostAsync();
    host.Coordinator.Windows.Enqueue(_window(0, 10, BILLING_TYPE));
    host.Coordinator.Windows.Enqueue(_window(10, 20, BILLING_TYPE));
    host.Coordinator.Result = _ => new OriginReattributionResult { Advanced = false };

    var batches = await _worker(host).RunPassAsync(CancellationToken.None);

    await Assert.That(batches).IsEqualTo(0);
    await Assert.That(host.Coordinator.Batches.Count).IsEqualTo(1)
      .Because("another walk or a rewalk moved the cursor; the next pass starts from where the store says");
  }

  [Test]
  public async Task Pass_OnAStoreWithoutReattribution_IsInertAsync() {
    await using var host = await _hostAsync();
    host.Coordinator.Windows.Enqueue(null);

    await Assert.That(await _worker(host).RunPassAsync(CancellationToken.None)).IsEqualTo(0);
    await Assert.That(host.Coordinator.Batches).IsEmpty();
  }

  [Test]
  public async Task Pass_WhenTheStoreCannotReattribute_StopsAsync() {
    await using var host = await _hostAsync();
    host.Coordinator.Windows.Enqueue(_window(0, 10, BILLING_TYPE));
    host.Coordinator.Windows.Enqueue(_window(10, 20, BILLING_TYPE));
    host.Coordinator.Result = _ => null;

    await Assert.That(await _worker(host).RunPassAsync(CancellationToken.None)).IsEqualTo(0);
    await Assert.That(host.Coordinator.Batches.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Pass_WithNoOwnedDomains_LeavesTheStoreAsItWasAsync() {
    await using var host = await _hostAsync(ownedDomains: []);
    host.Coordinator.Windows.Enqueue(_window(0, 10, BILLING_TYPE));

    await Assert.That(await _worker(host).RunPassAsync(CancellationToken.None)).IsEqualTo(0);
    await Assert.That(host.Coordinator.WindowReads).IsEmpty()
      .Because("a service with no owned domains keeps the local lane it always had; there is nothing to re-attribute");
  }

  [Test]
  public async Task Pass_OnAHostWithNoRoutingOptions_LeavesTheStoreAsItWasAsync() {
    await using var host = await _hostAsync(routing: false);
    host.Coordinator.Windows.Enqueue(_window(0, 10, BILLING_TYPE));

    await Assert.That(await _worker(host).RunPassAsync(CancellationToken.None)).IsEqualTo(0);
    await Assert.That(host.Coordinator.WindowReads).IsEmpty()
      .Because("a host that never configured routing owns no domains, so every event without an origin is its own");
  }

  [Test]
  public async Task Pass_WhenDisabled_DoesNothingAsync() {
    await using var host = await _hostAsync();

    await Assert.That(await _worker(host, new StreamIntegrityOptions { ReattributionEnabled = false })
      .RunPassAsync(CancellationToken.None)).IsEqualTo(0);
    await Assert.That(host.Coordinator.WindowReads).IsEmpty();
  }

  [Test]
  public async Task Pass_OnAnInstanceThatDoesNotHoldTheMaintainerDuty_DoesNothingAsync() {
    await using var host = await _hostAsync(maintainer: false);

    await _worker(host).RunPassAsync(CancellationToken.None);

    await Assert.That(host.Coordinator.WindowReads).IsEmpty();
  }

  [Test]
  public async Task Pass_OnAHostWithNoCoordinator_IsInertAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OriginReattributionWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(new StreamIntegrityOptions()),
      NullLogger<OriginReattributionWorker>.Instance);

    await Assert.That(await worker.RunPassAsync(CancellationToken.None)).IsEqualTo(0);
  }

  [Test]
  public async Task Pass_OnABusyService_WaitsUntilMaxDeferral_ThenIsForcedThroughAsync() {
    await using var host = await _hostAsync();
    host.Coordinator.Backlog = _busy;
    var worker = _worker(host);

    await worker.RunPassAsync(CancellationToken.None);
    var readsWhileBusy = host.Coordinator.WindowReads.Count;
    host.Clock.Advance(TimeSpan.FromHours(1));
    await worker.RunPassAsync(CancellationToken.None);

    await Assert.That(readsWhileBusy).IsEqualTo(0).Because("re-attribution competes with live traffic for the same store");
    await Assert.That(host.Coordinator.WindowReads.Count).IsEqualTo(1).Because("a service that never goes quiet is still re-attributed");
    await Assert.That(host.Housekeeping.TryBegin(HousekeepingCoordinator.Activity.Integrity, new ServiceBacklog()).Granted)
      .IsTrue().Because("the pass releases the integrity slot when it ends");
  }

  [Test]
  public async Task Pass_CountsReattributedUnattributedAndDisagreeingRowsAsync() {
    await using var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: meterFactory.GetRequiredService<IMeterFactory>()));
    var readings = new Dictionary<string, long>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, metrics.GapsDetected.Meter)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => {
      lock (readings) {
        readings[instrument.Name] = value;
      }
    });
    listener.Start();
    await using var host = await _hostAsync(metrics: metrics);
    host.Coordinator.Windows.Enqueue(_window(0, 10, BILLING_TYPE));
    host.Coordinator.Result = _ => new OriginReattributionResult { Reattributed = 7, HopDisagreements = 2, Unattributed = 3, Advanced = true };

    await _worker(host).RunPassAsync(CancellationToken.None);
    listener.RecordObservableInstruments();

    await Assert.That(readings.GetValueOrDefault("whizbang.stream_integrity.origins_reattributed")).IsEqualTo(7L);
    await Assert.That(readings.GetValueOrDefault("whizbang.stream_integrity.origins_unattributed")).IsEqualTo(3L);
    await Assert.That(readings.GetValueOrDefault("whizbang.stream_integrity.reattribution_hop_disagreements")).IsEqualTo(2L);
  }

  [Test]
  public async Task Pass_NamesAnUnclaimedTypeOnce_AcrossPassesAsync() {
    await using var host = await _hostAsync();
    var logger = new CaptureLogger();
    var worker = _worker(host, logger: logger);
    host.Coordinator.Windows.Enqueue(_window(0, 10, UNCLAIMED_TYPE));
    await worker.RunPassAsync(CancellationToken.None);
    host.Coordinator.Windows.Enqueue(_window(10, 20, UNCLAIMED_TYPE));

    await worker.RunPassAsync(CancellationToken.None);

    await Assert.That(logger.Messages.Count(m => m.Contains(UNCLAIMED_TYPE, StringComparison.Ordinal))).IsEqualTo(1)
      .Because("the unmapped type is reported once, not once per batch");
  }

  [Test]
  public async Task Pass_LogsHopDisagreementsAsync() {
    await using var host = await _hostAsync();
    var logger = new CaptureLogger();
    host.Coordinator.Windows.Enqueue(_window(0, 10, BILLING_TYPE));
    host.Coordinator.Result = _ => new OriginReattributionResult { HopDisagreements = 4, Advanced = true };

    await _worker(host, logger: logger).RunPassAsync(CancellationToken.None);

    await Assert.That(logger.Messages.Any(m => m.Contains('4', StringComparison.Ordinal)
                                            && m.Contains("first hop", StringComparison.OrdinalIgnoreCase))).IsTrue()
      .Because("ownership is the primary signal and the first hop a cross-check: a disagreement is logged and the row left");
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();

    await Assert.That(() => new OriginReattributionWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), null!, NullLogger<OriginReattributionWorker>.Instance))
      .Throws<ArgumentNullException>();
  }

  private sealed class CaptureLogger : ILogger<OriginReattributionWorker> {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (Messages) {
        Messages.Add(formatter(state, exception));
      }
    }
  }
}
