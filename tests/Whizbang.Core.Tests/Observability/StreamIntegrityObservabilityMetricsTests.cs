// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Rules;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// #2365: the instruments section 2.7 of the integrity plan still lacked: whether each re-check is on, when it last ran
/// and how much of its lookback it covered, and per lane how far epochs are sealed and how long ago the seal last moved.
/// Every one is bounded: no stream, tenant or event attribute.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/StreamIntegrityMetrics.cs</code-under-test>
/// <docs>resilience/stream-integrity#integrity-instruments</docs>
public class StreamIntegrityObservabilityMetricsTests {
  private const string ENABLED = "whizbang.stream_integrity.recheck_enabled";
  private const string LAST_RUN = "whizbang.stream_integrity.recheck_last_run_timestamp";
  private const string COVERAGE = "whizbang.stream_integrity.recheck_coverage_ratio";
  private const string FRONTIER = "whizbang.stream_integrity.epoch_frontier";
  private const string SEAL_AGE = "whizbang.stream_integrity.seal_age_seconds";
  private const string OPEN_WINDOW = "whizbang.stream_integrity.open_window_events";
  private const string GENERATION = "whizbang.stream_integrity.origin_generation";
  private const string CARRIED = "whizbang.stream_integrity.carried_streams";
  private const string LOWERED = "whizbang.stream_integrity.seal_lowered";
  private const string COMPACTIONS = "whizbang.stream_integrity.carried_compactions";

  private static WhizbangMetrics _root() =>
    new(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>());

  private static Meter _meterOf(StreamIntegrityMetrics metrics) => metrics.RepairsUnhealable.Meter;

  private static Dictionary<string, double> _byCheck(List<(double Value, Dictionary<string, object?> Tags)> series) =>
    series.ToDictionary(s => (string)s.Tags[StreamIntegrityMetrics.CHECK_TAG]!, s => s.Value);

  [Test]
  public async Task BeforeAnyReading_NoSeriesIsReported_NeverAZeroAsync() {
    var metrics = new StreamIntegrityMetrics(_root());

    foreach (var name in new[] { ENABLED, FRONTIER }) {
      await Assert.That(ProbeMeterReader.ReadSeries(_meterOf(metrics), name)).IsEmpty();
    }
    foreach (var name in new[] { LAST_RUN, COVERAGE, SEAL_AGE }) {
      await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), name)).IsEmpty()
        .Because("a check that never ran, or an instance that does not read the seals, reports nothing rather than zero");
    }
  }

  [Test]
  public async Task WhetherEachCheckIsOn_IsOneSeriesPerCheckAsync() {
    var metrics = new StreamIntegrityMetrics(_root());

    metrics.UpdateChecksEnabled(sealRecheck: true, fullHistoryCompare: false);

    var series = ProbeMeterReader.ReadSeries(_meterOf(metrics), ENABLED)
      .ToDictionary(s => (string)s.Tags[StreamIntegrityMetrics.CHECK_TAG]!, s => s.Value);
    await Assert.That(series[StreamIntegrityMetrics.CHECK_SEAL_RECHECK]).IsEqualTo(1L);
    await Assert.That(series[StreamIntegrityMetrics.CHECK_FULL_HISTORY_COMPARE]).IsEqualTo(0L);
  }

  [Test]
  public async Task WhenACheckLastRan_IsItsUnixTime_PerCheckAsync() {
    var metrics = new StreamIntegrityMetrics(_root());
    var at = new DateTimeOffset(2026, 11, 17, 3, 0, 0, TimeSpan.Zero);

    metrics.RecordCheckRan(StreamIntegrityMetrics.CHECK_SEAL_RECHECK, at);
    metrics.RecordCheckRan(StreamIntegrityMetrics.CHECK_SEAL_RECHECK, at.AddHours(1));

    var series = _byCheck(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), LAST_RUN));
    await Assert.That(series.Keys.ToList()).IsEquivalentTo([StreamIntegrityMetrics.CHECK_SEAL_RECHECK])
      .Because("a check that never ran has no last run");
    await Assert.That(series[StreamIntegrityMetrics.CHECK_SEAL_RECHECK]).IsEqualTo(at.AddHours(1).ToUnixTimeSeconds());
  }

  [Test]
  public async Task ASealReading_IsTheFrontierAndAgePerLane_AndTheRecheckCoverageAsync() {
    var metrics = new StreamIntegrityMetrics(_root());
    var lane = (Guid)TrackedGuid.New();

    metrics.UpdateSealReading(new IntegritySealReading {
      Reported = true,
      Lanes = [new LaneSeal(lane, SealedThrough: 300_000, AgeSeconds: 42.5), new LaneSeal(Guid.Empty, 100_000, 7)],
      SealRecheckCoverageRatio = 0.25,
    });

    var frontier = ProbeMeterReader.ReadSeries(_meterOf(metrics), FRONTIER)
      .ToDictionary(s => (string)s.Tags["origin_service_id"]!, s => s.Value);
    await Assert.That(frontier[lane.ToString()]).IsEqualTo(300_000L);
    await Assert.That(frontier[Guid.Empty.ToString()]).IsEqualTo(100_000L).Because("the local lane is the empty id");
    var age = ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), SEAL_AGE)
      .ToDictionary(s => (string)s.Tags["origin_service_id"]!, s => s.Value);
    await Assert.That(age[lane.ToString()]).IsEqualTo(42.5);
    var coverage = _byCheck(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), COVERAGE));
    await Assert.That(coverage[StreamIntegrityMetrics.CHECK_SEAL_RECHECK]).IsEqualTo(0.25);
  }

  [Test]
  public async Task AReadingWithNothingSealedInTheLookback_ReportsNoCoverage_AndAWithdrawnReadingNothingAtAllAsync() {
    var metrics = new StreamIntegrityMetrics(_root());

    metrics.UpdateSealReading(new IntegritySealReading { Reported = true, Lanes = [new LaneSeal(Guid.Empty, 1, 1)] });
    await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), COVERAGE)).IsEmpty()
      .Because("no epoch sealed within the lookback means the share is undefined, not zero");
    await Assert.That(ProbeMeterReader.ReadSeries(_meterOf(metrics), FRONTIER).Count).IsEqualTo(1);

    metrics.UpdateSealReading(null);
    await Assert.That(ProbeMeterReader.ReadSeries(_meterOf(metrics), FRONTIER)).IsEmpty()
      .Because("an instance without the maintainer duty withdraws the reading");
    await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), SEAL_AGE)).IsEmpty();
  }

  [Test]
  public async Task ASealReading_AlsoReportsEachLanesOpenWindow_TheGeneration_AndTheCarriedStreamsAsync() {
    var metrics = new StreamIntegrityMetrics(_root());
    var lane = (Guid)TrackedGuid.New();

    metrics.UpdateSealReading(new IntegritySealReading {
      Reported = true,
      Lanes = [new LaneSeal(lane, 300, 1, OpenWindowEvents: 42), new LaneSeal(Guid.Empty, 100, 1, OpenWindowEvents: 9, OpenWindowCapped: true)],
      OriginGeneration = 7,
      CarriedStreams = 1500,
      CarriedStreamsCapped = false,
    });

    var open = ProbeMeterReader.ReadSeries(_meterOf(metrics), OPEN_WINDOW)
      .ToDictionary(s => (string)s.Tags["origin_service_id"]!, s => (s.Value, (bool)s.Tags["capped"]!));
    await Assert.That(open[lane.ToString()]).IsEqualTo((42L, false));
    await Assert.That(open[Guid.Empty.ToString()]).IsEqualTo((9L, true)).Because("a count that stopped at its cap says so");
    await Assert.That(ProbeMeterReader.ReadSeries(_meterOf(metrics), GENERATION).Single().Value).IsEqualTo(7L);
    var carried = ProbeMeterReader.ReadSeries(_meterOf(metrics), CARRIED).Single();
    await Assert.That(carried.Value).IsEqualTo(1500L);
    await Assert.That(carried.Tags["capped"]).IsEqualTo(false);
  }

  [Test]
  public async Task AReadingWithoutTheNewParts_ReportsNoneOfThemAsync() {
    var metrics = new StreamIntegrityMetrics(_root());

    metrics.UpdateSealReading(new IntegritySealReading { Reported = true, Lanes = [new LaneSeal(Guid.Empty, 1, 1)] });

    await Assert.That(ProbeMeterReader.ReadSeries(_meterOf(metrics), OPEN_WINDOW)).IsEmpty()
      .Because("a lane whose open window was not counted reports nothing, not zero");
    await Assert.That(ProbeMeterReader.ReadSeries(_meterOf(metrics), GENERATION)).IsEmpty();
    await Assert.That(ProbeMeterReader.ReadSeries(_meterOf(metrics), CARRIED)).IsEmpty();
  }

  [Test]
  public async Task SealLowered_AndCarriedCompactions_ExistAtZeroForEveryValue_AndCountByItAsync() {
    var metrics = new StreamIntegrityMetrics(_root());

    var lowered = ProbeMeterReader.ReadSeries(_meterOf(metrics), LOWERED).ToDictionary(s => (string)s.Tags["reason"]!, s => s.Value);
    await Assert.That(lowered.Keys.Order().ToList()).IsEquivalentTo(["rewrite", "rule_change"]);
    var compactions = ProbeMeterReader.ReadSeries(_meterOf(metrics), COMPACTIONS).ToDictionary(s => (string)s.Tags["kind"]!, s => s.Value);
    await Assert.That(compactions.Keys.Order().ToList()).IsEquivalentTo(["archive", "truncate"]);
    await Assert.That(lowered.Values.Sum() + compactions.Values.Sum()).IsEqualTo(0L);

    metrics.RecordSealLowered(StreamIntegrityMetrics.SEAL_LOWERED_REWRITE);
    metrics.RecordCarriedCompaction(archived: true);
    metrics.RecordCarriedCompaction(archived: false);
    metrics.RecordCarriedCompaction(archived: false);

    lowered = ProbeMeterReader.ReadSeries(_meterOf(metrics), LOWERED).ToDictionary(s => (string)s.Tags["reason"]!, s => s.Value);
    await Assert.That(lowered["rewrite"]).IsEqualTo(1L);
    compactions = ProbeMeterReader.ReadSeries(_meterOf(metrics), COMPACTIONS).ToDictionary(s => (string)s.Tags["kind"]!, s => s.Value);
    await Assert.That(compactions["archive"]).IsEqualTo(1L);
    await Assert.That(compactions["truncate"]).IsEqualTo(2L);
  }

  /// <summary>
  /// The design choice that integrity instruments carry no per-stream or per-tenant attribute (#2365): a series per
  /// stream or tenant grows with the data, not the fleet. Every attribute any integrity instrument reports, the pair
  /// guard's included, is one of a bounded set: a service, a lane, a state, a reason, a check, a kind.
  /// </summary>
  /// <docs>resilience/stream-integrity#integrity-instruments</docs>
  [Test]
  public async Task EveryIntegrityInstrumentsAttribute_IsBounded_NoneNamesAStreamOrATenantAsync() {
    var root = _root();
    var metrics = new StreamIntegrityMetrics(root);
    var guard = new IntegrityPairGuard(CatalogRuleFingerprintSource.Empty, Options.Create(new StreamIntegrityOptions()),
      NullLogger<IntegrityPairGuard>.Instance, metrics: root);
    _ = guard.Admit("peer-svc", IntegrityPairSide.Consumer, null);   // a halt: every pair-guard series reports
    metrics.UpdateChecksEnabled(sealRecheck: true, fullHistoryCompare: true);
    metrics.RecordCheckRan(StreamIntegrityMetrics.CHECK_FULL_HISTORY_COMPARE, DateTimeOffset.UnixEpoch);
    metrics.UpdateSealReading(new IntegritySealReading {
      Reported = true,
      Lanes = [new LaneSeal(Guid.Empty, 1, 1, OpenWindowEvents: 1)],
      SealRecheckCoverageRatio = 1,
      OriginGeneration = 1,
      CarriedStreams = 1,
    });
    metrics.RecordSealLowered(StreamIntegrityMetrics.SEAL_LOWERED_RULE_CHANGE);
    metrics.RecordCarriedCompaction(archived: false);
    metrics.UpdateLedgerBacklog(new LedgerBacklogSnapshot { Reported = true, Buckets = [new LedgerBacklogCount("o", "open", 1)] });

    var keys = new Dictionary<string, HashSet<string>>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter.Name == StreamIntegrityMetrics.METER_NAME && instrument.Meter.Scope == root.MeterFactory) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      if (!keys.TryGetValue(instrument.Name, out var set)) {
        set = [];
        keys[instrument.Name] = set;
      }
      foreach (var tag in tags) {
        set.Add(tag.Key);
      }
    }
    listener.SetMeasurementEventCallback<long>((i, _, t, _) => Record(i, t));
    listener.SetMeasurementEventCallback<double>((i, _, t, _) => Record(i, t));
    listener.Start();
    listener.RecordObservableInstruments();

    string[] bounded = [
      "check", "origin_service_id", "origin", "peer", "side", "state", "reason", "fingerprint", "kind", "how", "role",
      "source", "level", "outcome", "delivery", "event_type", "perspective", "sweep", "windowed", "recompute", "type",
      "domain", "capped",
    ];
    var all = keys.SelectMany(k => k.Value.Select(v => $"{k.Key}:{v}")).ToList();
    await Assert.That(keys.Keys).Contains(FRONTIER).Because("positive control: the listener sees the new instruments");
    await Assert.That(keys.Keys).Contains("whizbang.stream_integrity.pair_state");
    await Assert.That(all.Where(k => !bounded.Contains(k[(k.IndexOf(':', StringComparison.Ordinal) + 1)..])).ToList()).IsEmpty();
    foreach (var name in new[] { ENABLED, LAST_RUN, COVERAGE }) {
      await Assert.That(keys[name]).IsEquivalentTo([StreamIntegrityMetrics.CHECK_TAG]);
    }
    foreach (var name in new[] { FRONTIER, SEAL_AGE }) {
      await Assert.That(keys[name]).IsEquivalentTo(["origin_service_id"]);
    }
    await Assert.That(keys[OPEN_WINDOW]).IsEquivalentTo(["origin_service_id", "capped"]);
    await Assert.That(keys[CARRIED]).IsEquivalentTo(["capped"]);
    await Assert.That(keys[GENERATION]).IsEmpty().Because("one generation per service");
    await Assert.That(keys[LOWERED]).IsEquivalentTo(["reason"]);
    await Assert.That(keys[COMPACTIONS]).IsEquivalentTo(["kind"]);
  }
}
