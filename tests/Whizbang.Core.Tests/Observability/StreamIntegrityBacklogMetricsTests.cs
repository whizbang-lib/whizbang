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
/// #2256: the open repair backlog as gauges, and the buckets opened and closed as counters, so completion
/// (<c>closed / opened</c>) and an ETA (<c>ledger_open_buckets / rate(closed)</c>) are computable in any metrics backend.
/// The ledger deletes a row when its bucket heals, so it never holds a starting total; these instruments are the only
/// way to see progress without a database query.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/StreamIntegrityMetrics.cs</code-under-test>
/// <docs>resilience/stream-integrity#repair-backlog-and-eta</docs>
public class StreamIntegrityBacklogMetricsTests {
  private const string OPEN_BUCKETS = "whizbang.stream_integrity.ledger_open_buckets";
  private const string OUTBOUND = "whizbang.stream_integrity.outbound_deliveries";
  private const string OPENED = "whizbang.stream_integrity.ledger_buckets_opened";
  private const string CLOSED = "whizbang.stream_integrity.ledger_buckets_closed";

  private static readonly string[] _openedHows = ["new", "reopened"];
  private static readonly string[] _closedHows = ["forgotten", "healed", "unhealable"];

  private static StreamIntegrityMetrics _metrics() =>
    new(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  private static Meter _meterOf(StreamIntegrityMetrics metrics) => metrics.RepairsUnhealable.Meter;

  [Test]
  public async Task TheBacklogInstruments_AreCreatedWithTheirNamesAsync() {
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

    await Assert.That(names).Contains(OPEN_BUCKETS).Because("dashboards and alerts name these instruments");
    await Assert.That(names).Contains(OUTBOUND);
    await Assert.That(names).Contains(OPENED);
    await Assert.That(names).Contains(CLOSED);
  }

  [Test]
  public async Task TheCounters_ExistAtZeroForEveryHow_BeforeTheFirstCountAsync() {
    var metrics = _metrics();

    var opened = _byTag(ProbeMeterReader.ReadSeries(_meterOf(metrics), OPENED), "how");
    var closed = _byTag(ProbeMeterReader.ReadSeries(_meterOf(metrics), CLOSED), "how");

    await Assert.That(opened.Keys.Order().ToArray()).IsEquivalentTo(_openedHows)
      .Because("a rate over a series that does not exist yet reads as no data, not as zero");
    await Assert.That(closed.Keys.Order().ToArray()).IsEquivalentTo(_closedHows);
    await Assert.That(opened.Values.Sum() + closed.Values.Sum()).IsEqualTo(0L);
  }

  [Test]
  public async Task NoReading_ReportsNoSeries_NeverAZeroAsync() {
    var metrics = _metrics();

    await Assert.That(ProbeMeterReader.ReadSeries(_meterOf(metrics), OPEN_BUCKETS)).IsEmpty()
      .Because("an instance that does not hold the duty must not report 0 open buckets: it would read as healed");
    await Assert.That(ProbeMeterReader.ReadSeries(_meterOf(metrics), OUTBOUND)).IsEmpty();
  }

  [Test]
  public async Task AReportedReading_IsOneSeriesPerOriginAndState_AndTheQueueDepthAsync() {
    var metrics = _metrics();

    metrics.UpdateLedgerBacklog(new LedgerBacklogSnapshot {
      Reported = true,
      Buckets = [
        new LedgerBacklogCount("orders", LedgerBacklogStates.OPEN, 40),
        new LedgerBacklogCount("orders", LedgerBacklogStates.UNHEALABLE, 3),
        new LedgerBacklogCount("billing", LedgerBacklogStates.WAITING, 0),
      ],
      OutboundDeliveries = 4_100,
    });

    var series = ProbeMeterReader.ReadSeries(_meterOf(metrics), OPEN_BUCKETS);
    await Assert.That(series.Count).IsEqualTo(3);
    await Assert.That(_value(series, "orders", LedgerBacklogStates.OPEN)).IsEqualTo(40L);
    await Assert.That(_value(series, "orders", LedgerBacklogStates.UNHEALABLE)).IsEqualTo(3L);
    await Assert.That(_value(series, "billing", LedgerBacklogStates.WAITING)).IsEqualTo(0L)
      .Because("a state with nothing in it reads zero, so a healed origin falls to zero instead of going stale");
    var outbound = ProbeMeterReader.ReadSeries(_meterOf(metrics), OUTBOUND);
    await Assert.That(outbound.Count).IsEqualTo(1);
    await Assert.That(outbound[0].Value).IsEqualTo(4_100L);
  }

  [Test]
  public async Task WithdrawingTheReading_StopsBothGaugesAsync() {
    var metrics = _metrics();
    metrics.UpdateLedgerBacklog(new LedgerBacklogSnapshot {
      Reported = true,
      Buckets = [new LedgerBacklogCount("orders", LedgerBacklogStates.OPEN, 40)],
      OutboundDeliveries = 1,
    });

    metrics.UpdateLedgerBacklog(null);

    await Assert.That(metrics.CurrentLedgerBacklogForTest.Reported).IsFalse();
    await Assert.That(ProbeMeterReader.ReadSeries(_meterOf(metrics), OPEN_BUCKETS)).IsEmpty()
      .Because("an instance that lost the duty stops reporting, or two instances would report the same backlog");
    await Assert.That(ProbeMeterReader.ReadSeries(_meterOf(metrics), OUTBOUND)).IsEmpty();
  }

  [Test]
  public async Task BucketsMarkedUnhealable_CountAsRepairsUnhealable_AndAsClosedAsync() {
    var metrics = _metrics();

    metrics.RecordBucketsMarkedUnhealable(5);
    metrics.RecordBucketsMarkedUnhealable(0);

    await Assert.That(ProbeMeterReader.ReadTotal(_meterOf(metrics), "whizbang.stream_integrity.repairs_unhealable")).IsEqualTo(5L);
    var closed = _byTag(ProbeMeterReader.ReadSeries(_meterOf(metrics), CLOSED), "how");
    await Assert.That(closed["unhealable"]).IsEqualTo(5L)
      .Because("a bucket repair cannot change is settled; the backlog's completion counts it closed");
    await Assert.That(closed["healed"]).IsEqualTo(0L);
  }

  private static Dictionary<string, long> _byTag(List<(long Value, Dictionary<string, object?> Tags)> series, string tag) {
    var result = new Dictionary<string, long>(StringComparer.Ordinal);
    foreach (var (value, tags) in series) {
      if (tags.TryGetValue(tag, out var v) && v is string key) {
        result[key] = value;
      }
    }
    return result;
  }

  private static long _value(List<(long Value, Dictionary<string, object?> Tags)> series, string origin, string state) =>
    series.Single(s => Equals(s.Tags["origin"], origin) && Equals(s.Tags["state"], state)).Value;
}
