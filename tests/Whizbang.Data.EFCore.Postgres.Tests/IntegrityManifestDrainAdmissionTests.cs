// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #1792: with the paced repair drain on, a manifest comparison used to write every deficit bucket
/// of every chunk into the repair ledger the drain dispatches from. <c>MaxAutoRepairRequestsPerAudit</c>
/// was only consulted on the burst path, so with the drain on it capped nothing, and nothing bounded
/// the ledger's total across audits.
/// </summary>
/// <remarks>
/// <para>
/// These tests lock the admission rule. A comparison admits NEW deficit buckets up to the
/// per-comparison cap and up to the headroom left under <c>MaxRepairDrainBacklog</c>. A bucket the
/// ledger already tracks is refreshed without counting, because re-admitting it adds nothing (and
/// counting it would let a handful of stuck buckets at the head of every manifest starve the rest).
/// Over-cap buckets are deferred, never dropped: nothing is written for them, they stay divergent,
/// and a later audit re-detects them. Every deferral is logged and counted.
/// </para>
/// <para>
/// Every arm of the admission code is taken inside this class, because the CI coverage merge keeps
/// the best per-line condition count of any single shard. No database: the ledger and the
/// coordinator are recording fakes. The receptor's compare gate is process-wide, hence the shared
/// not-in-parallel key with the sibling suites.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs</code-under-test>
/// <docs>resilience/stream-integrity#repair-caps</docs>
[NotInParallel("IntegrityManifestGates")]
[Category("Shard3")]
public class IntegrityManifestDrainAdmissionTests {

  private const string TYPE_X = "Contracts.TypeX";
  private const string DEFERRED_LOG = "Deferred";

  // ── the per-comparison cap ──────────────────────────────────────────────

  [Test]
  public async Task DrainOn_NewDeficitsPastThePerComparisonCap_AreNotAddedToTheLedgerAsync() {
    var streams = _streams(5);
    var harness = new Harness(_drain(perComparison: 2, backlog: 100)) {
      Probe = (_, keys, _) => new IntegrityRepairBacklogProbe(0, [.. keys.Select(_ => false)]),
    };

    await harness.CompareAsync(streams);

    await Assert.That(harness.Ledger.ReportedStreams).IsEquivalentTo(streams.Take(2))
      .Because("with the drain on, a ledger row IS a queued repair; the cap has to bound the rows one comparison adds");
    await Assert.That(harness.Coordinator.StampedStreams).IsEquivalentTo(streams.Take(2))
      .Because("only admitted buckets are stamped for the drain");
    await Assert.That(harness.Logger.Warnings(DEFERRED_LOG).Count).IsEqualTo(1)
      .Because("a deferral the operator cannot see is a silent swallow");
    await Assert.That(harness.Logger.Warnings(DEFERRED_LOG)[0]).Contains("Deferred 3 new deficit bucket(s)");
  }

  [Test]
  public async Task DrainOn_TrackedDeficits_AreRefreshedWithoutSpendingTheCapAsync() {
    var streams = _streams(4);
    var harness = new Harness(_drain(perComparison: 1, backlog: 100)) {
      // The first two buckets are already in the ledger (seen by an earlier audit).
      Probe = (_, keys, _) => new IntegrityRepairBacklogProbe(2, [.. keys.Select((_, i) => i < 2)]),
    };

    await harness.CompareAsync(streams);

    await Assert.That(harness.Ledger.ReportedStreams).IsEquivalentTo(streams.Take(3))
      .Because("tracked buckets add nothing to the backlog, so they must not starve the new ones of the allowance");
    await Assert.That(harness.Coordinator.StampedStreams).IsEquivalentTo(streams.Take(3));
    await Assert.That(harness.Logger.Warnings(DEFERRED_LOG)[0]).Contains("Deferred 1 new deficit bucket(s)");
  }

  [Test]
  public async Task DrainOn_UnderBothCaps_AdmitsEveryDeficit_AndLogsNoDeferralAsync() {
    var streams = _streams(3);
    var metrics = _newMetrics();
    using var probe = new MeterProbe(metrics.RepairsDeferred.Meter);
    var harness = new Harness(_drain(perComparison: 25, backlog: 100)) {
      Probe = (_, keys, _) => new IntegrityRepairBacklogProbe(0, [.. keys.Select(_ => false)]),
      Metrics = metrics,
    };

    await harness.CompareAsync(streams);

    await Assert.That(harness.Ledger.ReportedStreams).IsEquivalentTo(streams);
    await Assert.That(harness.Logger.Warnings(DEFERRED_LOG)).IsEmpty();
    await Assert.That(probe.CounterTotal(metrics.RepairsDeferred.Instrument)).IsEqualTo(0);
  }

  // ── the total backlog bound ─────────────────────────────────────────────

  [Test]
  public async Task DrainOn_BacklogAtTheBound_AdmitsNoNewDeficit_ButRefreshesTrackedOnesAsync() {
    var streams = _streams(3);
    var metrics = _newMetrics();
    using var probe = new MeterProbe(metrics.RepairsDeferred.Meter);
    var harness = new Harness(_drain(perComparison: 25, backlog: 10)) {
      Probe = (_, keys, _) => new IntegrityRepairBacklogProbe(10, [.. keys.Select((_, i) => i == 0)]),
      Metrics = metrics,
    };

    await harness.CompareAsync(streams);

    await Assert.That(harness.Ledger.ReportedStreams).IsEquivalentTo(streams.Take(1))
      .Because("the backlog is full: nothing new may join it, but a bucket already queued keeps its row current");
    await Assert.That(harness.Logger.Warnings(DEFERRED_LOG)[0]).Contains("drain backlog 10 of 10");
    await Assert.That(probe.CounterTotal(metrics.RepairsDeferred.Instrument)).IsEqualTo(2);
    await Assert.That(probe.Total(metrics.RepairsDeferred.Instrument, "reason", "backlog_cap")).IsEqualTo(2)
      .Because("the backlog bound, not the per-comparison cap, is what turned these away");
  }

  [Test]
  public async Task DrainOn_HeadroomBelowThePerComparisonCap_AdmitsOnlyTheHeadroomAsync() {
    var streams = _streams(5);
    var metrics = _newMetrics();
    using var probe = new MeterProbe(metrics.RepairsDeferred.Meter);
    var harness = new Harness(_drain(perComparison: 25, backlog: 10)) {
      Probe = (_, keys, _) => new IntegrityRepairBacklogProbe(8, [.. keys.Select(_ => false)]),
      Metrics = metrics,
    };

    await harness.CompareAsync(streams);

    await Assert.That(harness.Ledger.ReportedStreams).IsEquivalentTo(streams.Take(2))
      .Because("8 of 10 rows are taken; two more fill the backlog and the rest wait");
    await Assert.That(probe.Total(metrics.RepairsDeferred.Instrument, "reason", "backlog_cap")).IsEqualTo(3);
  }

  [Test]
  public async Task DrainOn_PerComparisonCapReached_CountsTheDeferralUnderThatReasonAsync() {
    var streams = _streams(4);
    var metrics = _newMetrics();
    using var probe = new MeterProbe(metrics.RepairsDeferred.Meter);
    var harness = new Harness(_drain(perComparison: 1, backlog: 100)) {
      Probe = (_, keys, _) => new IntegrityRepairBacklogProbe(0, [.. keys.Select(_ => false)]),
      Metrics = metrics,
    };

    await harness.CompareAsync(streams);

    await Assert.That(probe.Total(metrics.RepairsDeferred.Instrument, "reason", "per_audit_cap")).IsEqualTo(3);
    await Assert.That(probe.Total(metrics.RepairsDeferred.Instrument, "reason", "backlog_cap")).IsEqualTo(0);
  }

  [Test]
  public async Task DrainOn_DeferredDeficits_StillCountAsDetectedDivergencesAsync() {
    var streams = _streams(3);
    var metrics = _newMetrics();
    using var probe = new MeterProbe(metrics.DivergencesDetected.Meter);
    var harness = new Harness(_drain(perComparison: 1, backlog: 100)) {
      Probe = (_, keys, _) => new IntegrityRepairBacklogProbe(0, [.. keys.Select(_ => false)]),
      Metrics = metrics,
    };

    await harness.CompareAsync(streams);

    await Assert.That(probe.CounterTotal(metrics.DivergencesDetected.Instrument)).IsEqualTo(3)
      .Because("deferring the repair does not make the divergence any less detected");
  }

  [Test]
  public async Task DrainOn_TheProbeIsAskedForTheDeficitKeysAndTheBacklogBoundAsync() {
    var streams = _streams(2);
    var harness = new Harness(_drain(perComparison: 25, backlog: 1234)) {
      Probe = (_, keys, _) => new IntegrityRepairBacklogProbe(0, [.. keys.Select(_ => false)]),
    };

    await harness.CompareAsync(streams);

    await Assert.That(harness.Coordinator.ProbeCalls.Count).IsEqualTo(1);
    var (origin, keys, cap) = harness.Coordinator.ProbeCalls[0];
    await Assert.That(origin).IsEqualTo(harness.Coordinator.OriginId);
    await Assert.That(keys.Select(k => k.StreamId)).IsEquivalentTo(streams);
    await Assert.That(cap).IsEqualTo(1234)
      .Because("the count only has to reach the bound, which keeps the read bounded on a large ledger");
  }

  // ── degraded probe ──────────────────────────────────────────────────────

  [Test]
  public async Task DrainOn_ProbeUnsupported_FallsBackToThePerComparisonCapAsync() {
    var streams = _streams(5);
    var harness = new Harness(_drain(perComparison: 2, backlog: 1)) { Probe = null };

    await harness.CompareAsync(streams);

    await Assert.That(harness.Ledger.ReportedStreams).IsEquivalentTo(streams.Take(2))
      .Because("without a backlog reading the per-comparison cap still bounds what one comparison adds");
    await Assert.That(harness.Logger.Warnings(DEFERRED_LOG)[0]).Contains("drain backlog unknown");
  }

  [Test]
  public async Task DrainOn_ProbeAnsweringTheWrongArity_IsTreatedAsUnsupportedAsync() {
    var streams = _streams(3);
    var harness = new Harness(_drain(perComparison: 1, backlog: 100)) {
      // One flag for three keys: unusable, so every key is treated as new.
      Probe = (_, _, _) => new IntegrityRepairBacklogProbe(0, [true]),
    };

    await harness.CompareAsync(streams);

    await Assert.That(harness.Ledger.ReportedStreams).IsEquivalentTo(streams.Take(1));
  }

  // ── what the admission rule does not touch ──────────────────────────────

  [Test]
  public async Task DrainOn_NonDeficitDivergences_AreNeverDeferredAsync() {
    var deficit = _streams(2);
    var mismatch = Guid.NewGuid();
    var harness = new Harness(_drain(perComparison: 25, backlog: 1)) {
      Probe = (_, keys, _) => new IntegrityRepairBacklogProbe(1, [.. keys.Select(_ => false)]),
      // The consumer holds as many events as the origin, with a different fold: identity mismatch.
      Local = [_digest(mismatch, 77, 78, 2)],
    };

    await harness.CompareAsync([.. deficit, mismatch]);

    await Assert.That(harness.Ledger.ReportedStreams).IsEquivalentTo([mismatch])
      .Because("only a deficit is ever repaired, so only a deficit is subject to the drain's admission");
    await Assert.That(harness.Coordinator.ProbeCalls[0].Keys.Select(k => k.StreamId)).IsEquivalentTo(deficit)
      .Because("the probe is asked about the deficits alone");
  }

  [Test]
  public async Task DrainOn_EveryReportedDivergence_TellsTheLedgerWhetherItIsADeficitAsync() {
    // #1842: the drain claims ledger rows, and the ledger cannot tell a deficit from a surplus by
    // itself (its four digest halves are hashes, not counts), so the comparison must say which is which.
    var deficit = Guid.NewGuid();
    var mismatch = Guid.NewGuid();
    var surplus = Guid.NewGuid();
    var harness = new Harness(_drain(perComparison: 25, backlog: 100)) {
      Probe = (_, keys, _) => new IntegrityRepairBacklogProbe(0, [.. keys.Select(_ => false)]),
      Local = [_digest(mismatch, 77, 78, 2), _digest(surplus, 77, 78, 3)],
    };

    await harness.CompareAsync([deficit, mismatch, surplus]);

    var kinds = harness.Ledger.Observations.ToDictionary(o => o.Key.StreamId, o => o.IsDeficit);
    await Assert.That(kinds[deficit]).IsTrue()
      .Because("the origin holds events this store lacks: a redelivery can repair it, so the drain may claim it");
    await Assert.That(kinds[mismatch]).IsFalse()
      .Because("equal counts with different folds: a redelivery re-ships what is already here and changes nothing");
    await Assert.That(kinds[surplus]).IsFalse()
      .Because("this store holds more than the origin: asking the origin for more cannot remove anything");
  }

  [Test]
  public async Task DrainOn_NoDeficits_DoesNotProbeAsync() {
    var mismatch = Guid.NewGuid();
    var harness = new Harness(_drain(perComparison: 25, backlog: 100)) {
      Local = [_digest(mismatch, 77, 78, 2)],
    };

    await harness.CompareAsync([mismatch]);

    await Assert.That(harness.Coordinator.ProbeCalls).IsEmpty();
    await Assert.That(harness.Ledger.ReportedStreams).IsEquivalentTo([mismatch]);
  }

  [Test]
  public async Task DrainOff_TheBurstPathKeepsItsOwnCap_AndNeverProbesAsync() {
    var streams = _streams(5);
    var options = _drain(perComparison: 2, backlog: 1);
    options.RepairDrainEnabled = false;
    var harness = new Harness(options);

    await harness.CompareAsync(streams);

    await Assert.That(harness.Coordinator.ProbeCalls).IsEmpty();
    await Assert.That(harness.Ledger.ReportedStreams).IsEquivalentTo(streams)
      .Because("the burst path records every divergence and caps the requests it sends instead");
    await Assert.That(harness.Ledger.RepairGrantCap).IsEqualTo(2);
  }

  [Test]
  public async Task DrainOn_ReportOnly_RecordsEveryDeficit_AndNeverProbesAsync() {
    var streams = _streams(5);
    var options = _drain(perComparison: 2, backlog: 1);
    options.RepairMode = IntegrityRepairMode.ReportOnly;
    var harness = new Harness(options);

    await harness.CompareAsync(streams);

    await Assert.That(harness.Coordinator.ProbeCalls).IsEmpty()
      .Because("the drain sends nothing at ReportOnly, so there is no repair backlog to bound");
    await Assert.That(harness.Ledger.ReportedStreams).IsEquivalentTo(streams);
  }

  // ── fixture ─────────────────────────────────────────────────────────────

  private static StreamIntegrityOptions _drain(int perComparison, int backlog) => new() {
    RepairMode = IntegrityRepairMode.AutoRepairCapped,
    RepairDrainEnabled = true,
    MaxAutoRepairRequestsPerAudit = perComparison,
    MaxRepairDrainBacklog = backlog,
  };

  private static List<Guid> _streams(int count) => [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid())];

  private static StreamDigest _digest(Guid stream, long lo, long hi, int count) => new() {
    TenantScope = "tenant-a",
    EventType = TYPE_X,
    StreamId = stream,
    DigestLo = lo,
    DigestHi = hi,
    EventCount = count,
  };

  private static StreamIntegrityMetrics _newMetrics() =>
    new(new WhizbangMetrics(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  /// <summary>One wired receptor with recording collaborators.</summary>
  private sealed class Harness(StreamIntegrityOptions options) {
    public ProbeCoordinator Coordinator { get; } = new();
    public RecordingLedger Ledger { get; } = new();
    public CapturingLogger Logger { get; } = new();
    public Func<Guid, IReadOnlyList<IntegrityRepairLedger.DivergenceKey>, int, IntegrityRepairBacklogProbe?>? Probe { get; init; }
    public StreamIntegrityMetrics? Metrics { get; init; }
    public IReadOnlyList<StreamDigest> Local { get; init; } = [];

    public async Task CompareAsync(IReadOnlyList<Guid> originStreams) {
      Coordinator.Probe = Probe;
      Coordinator.Local = Local;
      var services = new ServiceCollection();
      services.AddSingleton<IWorkCoordinator>(Coordinator);
      services.AddSingleton<IDispatcher>(new NullDispatcher());
      services.AddSingleton<IIntegrityRepairLedger>(Ledger);
      services.AddSingleton(Options.Create(options));
      if (Metrics is not null) {
        services.AddSingleton(Metrics);
      }
      var sp = services.BuildServiceProvider();
      var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), Logger);
      await receptor.HandleAsync(new IntegrityManifest {
        ManifestStreamId = Coordinator.OriginId,
        OriginServiceId = Coordinator.OriginId,
        OriginServiceName = "origin-svc",
        Digests = [.. originStreams.Select(s => _digest(s, 11, 21, 2))],
        Level = ManifestLevel.Streams,
        SinceSequence = 100,
        ComputedThrough = 300,
        ChunkCount = 1,
      });
    }
  }

  /// <summary>Records report keys and the burst path's grant cap; reports every observation.</summary>
  private sealed class RecordingLedger : IIntegrityRepairLedger {
    public List<Guid> ReportedStreams { get; } = [];
    public List<IntegrityReportObservation> Observations { get; } = [];
    public int? RepairGrantCap { get; private set; }

    public ValueTask<bool> TryBeginReportAsync(IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    public ValueTask<bool> TryBeginRepairAsync(IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now,
        TimeSpan baseBackoff, int maxAttempts, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<bool>> TryBeginReportBatchAsync(IReadOnlyList<IntegrityReportObservation> observations,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) {
      ReportedStreams.AddRange(observations.Select(o => o.Key.StreamId));
      Observations.AddRange(observations);
      return ValueTask.FromResult<IReadOnlyList<bool>>([.. observations.Select(_ => false)]);
    }
    public ValueTask<IReadOnlyList<bool>> TryBeginRepairBatchAsync(IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts, int maxGrants, CancellationToken cancellationToken = default) {
      RepairGrantCap = maxGrants;
      return ValueTask.FromResult<IReadOnlyList<bool>>([.. keys.Select(_ => false)]);
    }
  }

  /// <summary>Answers the chunk fold from <see cref="Local"/>, records stamps and probes, and answers
  /// the probe from <see cref="Probe"/> (null: the default, unsupported).</summary>
  private sealed class ProbeCoordinator : IWorkCoordinator {
    public Guid OriginId { get; } = Guid.NewGuid();
    public IReadOnlyList<StreamDigest> Local { get; set; } = [];
    public Func<Guid, IReadOnlyList<IntegrityRepairLedger.DivergenceKey>, int, IntegrityRepairBacklogProbe?>? Probe { get; set; }
    public List<Guid> StampedStreams { get; } = [];
    public List<(Guid Origin, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> Keys, int Cap)> ProbeCalls { get; } = [];

    public Task<IntegrityRepairBacklogProbe?> IntegrityProbeRepairBacklogAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, int backlogCap,
        CancellationToken cancellationToken = default) {
      ProbeCalls.Add((originServiceId, keys, backlogCap));
      return Task.FromResult(Probe?.Invoke(originServiceId, keys, backlogCap));
    }

    public Task IntegrityStampRepairWindowsAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        long windowFrom, long windowUntil, CancellationToken cancellationToken = default) {
      StampedStreams.AddRange(keys.Select(k => k.StreamId));
      return Task.CompletedTask;
    }

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(Guid.Empty);

    public Task<IReadOnlyList<StreamDigest>?> ComputeStreamDigestsForChunkAsync(
      Guid originServiceId, IReadOnlyList<Guid> streamIds,
      long? sinceSequence, long? untilSequence, TimeSpan settleWindow,
      CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StreamDigest>?>(Local);

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

  /// <summary>Records warning-level log lines.</summary>
  private sealed class CapturingLogger : ILogger<IntegrityManifestReceptor> {
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public List<string> Warnings(string containing) {
      lock (_entries) {
        return [.. _entries
          .Where(e => e.Level == LogLevel.Warning && e.Message.Contains(containing, StringComparison.Ordinal))
          .Select(e => e.Message)];
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_entries) {
        _entries.Add((logLevel, formatter(state, exception)));
      }
    }
  }

  /// <summary>Reads one meter's instruments by identity, so sibling meters of the same name
  /// (other tests' metrics instances) never leak into a reading.</summary>
  private sealed class MeterProbe : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly List<(Instrument Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _readings = [];

    public MeterProbe(Meter meter) {
      _listener.InstrumentPublished = (instrument, listener) => {
        if (ReferenceEquals(instrument.Meter, meter)) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
        lock (_readings) {
          _readings.Add((instrument, value, tags.ToArray()));
        }
      });
      _listener.Start();
    }

    /// <summary>The cumulative total of a passive counter across all its series.</summary>
    public long CounterTotal(Instrument instrument) => _observe(instrument).Sum(r => r.Value);

    /// <summary>The cumulative total of the series carrying one tag value.</summary>
    public long Total(Instrument instrument, string tag, string value) =>
      _observe(instrument)
        .Where(r => r.Tags.Any(t => t.Key == tag && string.Equals(t.Value as string, value, StringComparison.Ordinal)))
        .Sum(r => r.Value);

    public void Dispose() => _listener.Dispose();

    private List<(Instrument Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _observe(Instrument instrument) {
      lock (_readings) {
        _readings.RemoveAll(r => ReferenceEquals(r.Instrument, instrument));
      }
      _listener.RecordObservableInstruments();
      lock (_readings) {
        return [.. _readings.Where(r => ReferenceEquals(r.Instrument, instrument))];
      }
    }
  }

  /// <summary>Report publishing is off in these tests, so the dispatcher is never called.</summary>
  private sealed class NullDispatcher : IDispatcher {
    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, DispatchOptions options, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, DispatchOptions options) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options) => throw new NotSupportedException();
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) => throw new NotSupportedException();
  }
}
