// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests (no Postgres) for the requester's side of a paced backfill window (#2092): a received window answer is
/// applied to the backfill's durable progress, its events are counted per type, and an answer with nothing more ends
/// the backfill; in report-only nothing is applied. The registrar joins the receptor to the integrity delivery drain.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BackfillWindowAnswerReceptor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RedeliveryRequestReceptorRegistrar.cs</code-under-test>
[Category("Shard2")]
public class BackfillWindowAnswerReceptorTests {
  private static readonly DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
  private const string PLACED = "App.Contracts.Orders.Placed, App.Contracts";

  [Test]
  public async Task Answer_IsAppliedToItsBackfill_AndItsEventsCountedByTypeAsync() {
    var answer = _answer(hasMore: true);
    var coordinator = new AnswerCoordinator { Result = _cursor(answer.BackfillId) with { EventsReceived = 1_000 } };
    var metrics = _metrics(out var meterServices);
    await using var ownedMeters = meterServices;
    using var received = new MetricProbe(metrics.BackfillEventsReceived.Instrument, "event_type");
    using var ended = new MetricProbe(metrics.BackfillsEnded.Instrument, "outcome");
    await using var sp = _provider(coordinator, metrics);

    await _receptor(sp).HandleAsync(answer);

    await Assert.That(coordinator.Recorded).IsEquivalentTo([(answer, _now)])
      .Because("the answer is applied on the service's clock, as it lands");
    await Assert.That(received.Total(PLACED)).IsEqualTo(1_000L);
    await Assert.That(ended.Sum()).IsEqualTo(0L).Because("more remains, so the backfill is still running");
  }

  [Test]
  public async Task FinalAnswer_EndsTheBackfill_AndCountsItCompleteAsync() {
    var answer = _answer(hasMore: false);
    var coordinator = new AnswerCoordinator {
      Result = _cursor(answer.BackfillId) with { CompletedAt = _now, Outcome = SubscriptionBackfillOutcome.Complete },
    };
    var metrics = _metrics(out var meterServices);
    await using var ownedMeters = meterServices;
    using var ended = new MetricProbe(metrics.BackfillsEnded.Instrument, "outcome");
    await using var sp = _provider(coordinator, metrics);

    await _receptor(sp).HandleAsync(answer);

    await Assert.That(ended.Total("complete")).IsEqualTo(1L);
  }

  [Test]
  public async Task AnAnswerThatAppliesToNothing_CountsNothingAsync() {
    var coordinator = new AnswerCoordinator { Result = null };
    var metrics = _metrics(out var meterServices);
    await using var ownedMeters = meterServices;
    using var received = new MetricProbe(metrics.BackfillEventsReceived.Instrument, "event_type");
    await using var sp = _provider(coordinator, metrics);

    await _receptor(sp).HandleAsync(_answer(hasMore: false));

    await Assert.That(coordinator.Recorded).Count().IsEqualTo(1);
    await Assert.That(received.Sum()).IsEqualTo(0L).Because("a late or duplicate answer was already counted, or belongs to nothing");
  }

  [Test]
  public async Task Answer_OnAHostWithoutMetrics_IsStillAppliedAsync() {
    var answer = _answer(hasMore: false);
    var coordinator = new AnswerCoordinator {
      Result = _cursor(answer.BackfillId) with { CompletedAt = _now, Outcome = SubscriptionBackfillOutcome.Complete },
    };
    await using var sp = _provider(coordinator, metrics: null);

    await _receptor(sp).HandleAsync(answer);

    await Assert.That(coordinator.Recorded).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Answer_InReportOnly_IsNotAppliedAsync() {
    var coordinator = new AnswerCoordinator();
    await using var sp = _provider(coordinator, metrics: null, repairMode: IntegrityRepairMode.ReportOnly);

    await _receptor(sp).HandleAsync(_answer(hasMore: true));

    await Assert.That(coordinator.Recorded).IsEmpty()
      .Because("report-only takes no part in backfill; the window times out and is asked again once repair is back on");
  }

  [Test]
  public async Task Answer_WithNoIntegrityOptionsRegistered_IsNotApplied_TheDefaultIsReportOnlyAsync() {
    var coordinator = new AnswerCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    await using var sp = services.BuildServiceProvider();

    await _receptor(sp).HandleAsync(_answer(hasMore: true));

    await Assert.That(coordinator.Recorded).IsEmpty();
  }

  [Test]
  public async Task Answer_OnAHostWithoutAClock_IsAppliedOnTheSystemClockAsync() {
    var answer = _answer(hasMore: true);
    var coordinator = new AnswerCoordinator { Result = _cursor(answer.BackfillId) };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton(Options.Create(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped }));
    await using var sp = services.BuildServiceProvider();

    await _receptor(sp).HandleAsync(answer);

    await Assert.That(coordinator.Recorded).Count().IsEqualTo(1);
    await Assert.That(coordinator.Recorded[0].Now).IsGreaterThan(DateTimeOffset.UnixEpoch);
  }

  [Test]
  public async Task Answer_WithoutACoordinator_IsIgnoredWithoutThrowingAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(Options.Create(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped }));
    await using var sp = services.BuildServiceProvider();

    await _receptor(sp).HandleAsync(_answer(hasMore: true));   // must not throw
  }

  [Test]
  public async Task Answer_Null_ThrowsAsync() {
    await using var sp = _provider(new AnswerCoordinator(), metrics: null);

    await Assert.That(async () => await _receptor(sp).HandleAsync(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Registrar_JoinsTheAnswerReceptorAtTheThreeStagesAsync() {
    var registry = new RecordingRegistry();
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(registry);
    await using var sp = services.BuildServiceProvider();
    var registrar = new RedeliveryRequestReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance,
      NullLogger<AuditRebuildRequestReceptor>.Instance, NullLogger<BackfillWindowAnswerReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);

    await Assert.That(registry.Registered.Where(r => r.Msg == typeof(BackfillWindowAnswer)).Select(r => r.Stage)).IsEquivalentTo([
      LifecycleStage.LocalImmediateInline, LifecycleStage.PreOutboxInline, LifecycleStage.PostInboxInline])
      .Because("the answer arrives over the inbox, and its receptor is what keeps it on receipt (#2091)");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static BackfillWindowAnswer _answer(bool hasMore) => new() {
    BackfillId = TrackedGuid.New().Value,
    Window = 3,
    EventCount = 1_000,
    FirstCommitSequence = 100,
    StoppedAtCommitSequence = 4_000,
    HasMore = hasMore,
  };

  private static SubscriptionBackfillCursor _cursor(Guid id) => new() {
    BackfillId = id,
    OwnerServiceId = Guid.Parse("0a000000-0000-0000-0000-00000000000a"),
    OwnerServiceName = "orders-svc",
    EventType = PLACED,
    WindowsRequested = 3,
  };

  private static BackfillWindowAnswerReceptor _receptor(ServiceProvider sp) =>
    new(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<BackfillWindowAnswerReceptor>.Instance);

  private static ServiceProvider _provider(AnswerCoordinator coordinator, StreamIntegrityMetrics? metrics,
      IntegrityRepairMode repairMode = IntegrityRepairMode.AutoRepairCapped) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<TimeProvider>(new FakeTimeProvider(_now));
    services.AddSingleton(Options.Create(new StreamIntegrityOptions { RepairMode = repairMode }));
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    return services.BuildServiceProvider();
  }

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

  private sealed class AnswerCoordinator : IWorkCoordinator {
    public SubscriptionBackfillCursor? Result { get; init; }
    public List<(BackfillWindowAnswer Answer, DateTimeOffset Now)> Recorded { get; } = [];

    public Task<SubscriptionBackfillCursor?> RecordSubscriptionBackfillAnswerAsync(
        BackfillWindowAnswer answer, DateTimeOffset now, CancellationToken cancellationToken = default) {
      Recorded.Add((answer, now));
      return Task.FromResult(Result);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
  }

  private sealed class RecordingRegistry : IReceptorRegistry {
    public List<(Type Msg, LifecycleStage Stage)> Registered { get; } = [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      Registered.Add((typeof(TMessage), stage));
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }
}
