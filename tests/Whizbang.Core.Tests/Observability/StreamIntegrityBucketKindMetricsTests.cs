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
/// #2260: the unhealed buckets split by kind. <c>unhealed_buckets</c> counts every ledger row, and a bucket whose two
/// sides hold the same number of events under different ids (or where this store holds more) read as data loss that
/// redelivery could never heal. The split names each kind; only a deficit is one repair heals. The untagged total stays,
/// for dashboards and alerts that already read it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/StreamIntegrityMetrics.cs</code-under-test>
/// <docs>resilience/stream-integrity#unhealed-buckets-by-kind</docs>
public class StreamIntegrityBucketKindMetricsTests {
  private const string BY_KIND = "whizbang.stream_integrity.unhealed_buckets_by_kind";
  private const string TOTAL = "whizbang.stream_integrity.unhealed_buckets";

  private static readonly string[] _kinds = ["deficit", "digest_mismatch", "surplus", "unknown"];

  private static StreamIntegrityMetrics _metrics() =>
    new(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  private static Meter _meterOf(StreamIntegrityMetrics metrics) => metrics.RepairsUnhealable.Meter;

  [Test]
  public async Task BeforeTheFirstReading_EveryKindReadsZeroAsync() {
    var metrics = _metrics();

    var byKind = _byKind(ProbeMeterReader.ReadSeries(_meterOf(metrics), BY_KIND));

    await Assert.That(byKind.Keys.Order().ToArray()).IsEquivalentTo(_kinds)
      .Because("the tag values dashboards name; each kind exists at zero, as the total does, so a healed kind falls to zero rather than going missing");
    await Assert.That(byKind.Values.Sum()).IsEqualTo(0L);
  }

  [Test]
  public async Task AReading_ReportsEachKind_AndKeepsTheUntaggedTotalAsync() {
    var metrics = _metrics();

    metrics.UpdateLedgerGauges(new LedgerGaugeSnapshot {
      UnhealedBuckets = 100_010,
      DeficitBuckets = 6,
      SurplusBuckets = 3,
      DigestMismatchBuckets = 100_000,
      UnknownKindBuckets = 1,
      UnhealedDeficitEvents = 42,
    });

    var byKind = _byKind(ProbeMeterReader.ReadSeries(_meterOf(metrics), BY_KIND));
    await Assert.That(byKind[LedgerBucketKinds.DEFICIT]).IsEqualTo(6L).Because("the only kind repair heals");
    await Assert.That(byKind[LedgerBucketKinds.SURPLUS]).IsEqualTo(3L);
    await Assert.That(byKind[LedgerBucketKinds.DIGEST_MISMATCH]).IsEqualTo(100_000L)
      .Because("equal counts under different ids are not missing data");
    await Assert.That(byKind[LedgerBucketKinds.UNKNOWN]).IsEqualTo(1L);
    var total = ProbeMeterReader.ReadSeries(_meterOf(metrics), TOTAL);
    await Assert.That(total.Count).IsEqualTo(1).Because("the total keeps its single untagged series");
    await Assert.That(total[0].Value).IsEqualTo(100_010L);
    await Assert.That(total[0].Tags).IsEmpty();
    await Assert.That(ProbeMeterReader.ReadTotal(_meterOf(metrics), "whizbang.stream_integrity.unhealed_deficit_events"))
      .IsEqualTo(42L).Because("the missing events are unchanged by the split");
  }

  private static Dictionary<string, long> _byKind(List<(long Value, Dictionary<string, object?> Tags)> series) {
    var result = new Dictionary<string, long>(StringComparer.Ordinal);
    foreach (var (value, tags) in series) {
      result[(string)tags[LedgerBucketKinds.TAG]!] = value;
    }
    return result;
  }
}
