// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// #2261: integrity completion reported directly. <c>verified_percent</c> (per origin, and all origins weighted by what
/// each committed), <c>repair_progress_percent</c> and <c>backlog_eta_seconds</c> are conveniences derived from base
/// meters; each reports no series at all until it has a reading, and a reading that has no value (nothing opened in the
/// window, a backlog that is not shrinking) reports none rather than a misleading number.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/StreamIntegrityMetrics.cs</code-under-test>
/// <docs>resilience/stream-integrity#derived-completion-meters</docs>
public class StreamIntegrityCompletionMetricsTests {
  private const string VERIFIED = "whizbang.stream_integrity.verified_percent";
  private const string VERIFIED_ALL = "whizbang.stream_integrity.verified_percent_all_origins";
  private const string PROGRESS = "whizbang.stream_integrity.repair_progress_percent";
  private const string ETA = "whizbang.stream_integrity.backlog_eta_seconds";

  private static StreamIntegrityMetrics _metrics() =>
    new(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  private static Meter _meterOf(StreamIntegrityMetrics metrics) => metrics.RepairsUnhealable.Meter;

  [Test]
  public async Task TheDerivedGauges_AreCreatedWithTheirNamesAsync() {
    var metrics = _metrics();
    var names = new List<string>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, _) => {
        if (instrument.Meter == _meterOf(metrics)) {
          names.Add(instrument.Name);
        }
      },
    };
    listener.Start();

    await Assert.That(names).Contains(VERIFIED).Because("dashboards and alerts name these instruments");
    await Assert.That(names).Contains(VERIFIED_ALL);
    await Assert.That(names).Contains(PROGRESS);
    await Assert.That(names).Contains(ETA);
  }

  [Test]
  public async Task BeforeAnyReading_NoDerivedGaugeReportsASeriesAsync() {
    var metrics = _metrics();

    foreach (var name in new[] { VERIFIED, VERIFIED_ALL, PROGRESS, ETA }) {
      await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), name)).IsEmpty()
        .Because($"{name} has no value before its first reading, and a zero would read as no progress at all");
    }
  }

  [Test]
  public async Task AVerifiedReading_IsOneSeriesPerOrigin_AndTheRollUpAsync() {
    var metrics = _metrics();
    var origin = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    metrics.UpdateVerifiedPercent(new IntegrityVerifiedReading {
      Reported = true,
      Origins = [new OriginVerifiedPercent(origin, 50.0)],
      AllOrigins = 95.5,
    });

    var perOrigin = ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), VERIFIED);
    await Assert.That(perOrigin.Count).IsEqualTo(1);
    await Assert.That(perOrigin[0].Value).IsEqualTo(50.0);
    await Assert.That(perOrigin[0].Tags["origin_service_id"]).IsEqualTo(origin.ToString())
      .Because("tagged as sealed_through is, so the two read side by side");
    var all = ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), VERIFIED_ALL);
    await Assert.That(all.Count).IsEqualTo(1);
    await Assert.That(all[0].Value).IsEqualTo(95.5);
  }

  [Test]
  public async Task AVerifiedReadingWithNoRollUp_OrWithdrawn_ReportsNothingForItAsync() {
    var metrics = _metrics();
    metrics.UpdateVerifiedPercent(new IntegrityVerifiedReading { Reported = true });

    await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), VERIFIED_ALL)).IsEmpty();

    metrics.UpdateVerifiedPercent(new IntegrityVerifiedReading {
      Reported = true,
      Origins = [new OriginVerifiedPercent(Guid.Empty, 10.0)],
      AllOrigins = 10.0,
    });
    metrics.UpdateVerifiedPercent(null);

    await Assert.That(metrics.CurrentVerifiedPercentForTest.Reported).IsFalse();
    await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), VERIFIED)).IsEmpty();
    await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), VERIFIED_ALL)).IsEmpty();
  }

  [Test]
  public async Task ARepairProgressReading_ReportsEachValueItHasAsync() {
    var metrics = _metrics();

    metrics.UpdateRepairProgress(new RepairProgressReading { Reported = true, ProgressPercent = 75.0, EtaSeconds = 10_800.0 });

    await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), PROGRESS).Single().Value).IsEqualTo(75.0);
    await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), ETA).Single().Value).IsEqualTo(10_800.0);

    metrics.UpdateRepairProgress(new RepairProgressReading { Reported = true, ProgressPercent = 120.0 });

    await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), PROGRESS).Single().Value).IsEqualTo(120.0);
    await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), ETA)).IsEmpty()
      .Because("a backlog that is not shrinking has no ETA, and reports none rather than a sentinel");
  }

  [Test]
  public async Task WithdrawingTheRepairProgress_StopsBothGaugesAsync() {
    var metrics = _metrics();
    metrics.UpdateRepairProgress(new RepairProgressReading { Reported = true, ProgressPercent = 75.0, EtaSeconds = 60.0 });

    metrics.UpdateRepairProgress(null);

    await Assert.That(metrics.CurrentRepairProgressForTest.Reported).IsFalse();
    await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), PROGRESS)).IsEmpty()
      .Because("one instance per service reports these, as it does the backlog they are computed from");
    await Assert.That(ProbeMeterReader.ReadDoubleSeries(_meterOf(metrics), ETA)).IsEmpty();
  }

  [Test]
  public async Task ThePendingLedgerTally_IsWhatTheCountersAddedSinceItWasLastMarkedAsync() {
    var metrics = _metrics();
    metrics.LedgerBucketsOpened.Add(7, new KeyValuePair<string, object?>(StreamIntegrityMetrics.HOW_TAG, StreamIntegrityMetrics.OPENED_NEW));
    metrics.LedgerBucketsOpened.Add(1, new KeyValuePair<string, object?>(StreamIntegrityMetrics.HOW_TAG, StreamIntegrityMetrics.OPENED_REOPENED));
    metrics.RecordBucketsMarkedUnhealable(2);
    metrics.LedgerBucketsClosed.Add(3, new KeyValuePair<string, object?>(StreamIntegrityMetrics.HOW_TAG, StreamIntegrityMetrics.CLOSED_HEALED));

    await Assert.That(metrics.PendingLedgerTally()).IsEqualTo((8L, 5L))
      .Because("every opening and closing the counters count, whoever added it, is tallied");

    metrics.MarkLedgerTallied(8, 5);
    metrics.LedgerBucketsClosed.Add(4, new KeyValuePair<string, object?>(StreamIntegrityMetrics.HOW_TAG, StreamIntegrityMetrics.CLOSED_FORGOTTEN));

    await Assert.That(metrics.PendingLedgerTally()).IsEqualTo((0L, 4L))
      .Because("what was written is not written again");
  }
}
