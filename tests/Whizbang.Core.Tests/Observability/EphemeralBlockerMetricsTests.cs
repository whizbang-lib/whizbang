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
/// #2250: <c>whizbang.ephemeral.blocked_events</c> reports the stored blocker report per ephemeral type and blocking type,
/// a pair that left the report reads zero rather than its last count, and the startup summary is claimed once.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
/// <code-under-test>src/Whizbang.Core/Observability/EphemeralBlockerMetrics.cs</code-under-test>
[Category("Observability")]
public class EphemeralBlockerMetricsTests {
  private const string PINGED = "Contracts.Sessions.SessionPinged, Contracts";
  private const string OPENED = "Contracts.Sessions.SessionOpened, Contracts";
  private const string CLOSED = "Contracts.Sessions.SessionClosed, Contracts";

  private static (EphemeralBlockerMetrics Metrics, IMeterFactory Factory) _newMetrics() {
    var factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
    return (new EphemeralBlockerMetrics(new WhizbangMetrics(factory)), factory);
  }

  private static List<(long Value, string Ephemeral, string Blocking)> _observe(IMeterFactory factory) {
    var observed = new List<(long, string, string)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Name == "whizbang.ephemeral.blocked_events" && ReferenceEquals(instrument.Meter.Scope, factory)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      var map = tags.ToArray().ToDictionary(t => t.Key, t => (string)t.Value!);
      observed.Add((value, map["ephemeral_type"], map["blocking_type"]));
    });
    listener.Start();
    listener.RecordObservableInstruments();
    return observed;
  }

  [Test]
  public async Task BlockedEvents_ReportsEachPair_TaggedByEphemeralAndBlockingTypeAsync() {
    var (metrics, factory) = _newMetrics();

    metrics.Publish([new EphemeralBlockedCount(PINGED, OPENED, 7), new EphemeralBlockedCount(PINGED, CLOSED, 2)]);

    var observed = _observe(factory);
    await Assert.That(observed).IsEquivalentTo([(7L, PINGED, OPENED), (2L, PINGED, CLOSED)]);
  }

  [Test]
  public async Task BlockedEvents_APairThatLeftTheReport_ReadsZero_NotItsLastCountAsync() {
    var (metrics, factory) = _newMetrics();
    metrics.Publish([new EphemeralBlockedCount(PINGED, OPENED, 7), new EphemeralBlockedCount(PINGED, CLOSED, 2)]);

    metrics.Publish([new EphemeralBlockedCount(PINGED, CLOSED, 1)]);

    var observed = _observe(factory);
    await Assert.That(observed).IsEquivalentTo([(0L, PINGED, OPENED), (1L, PINGED, CLOSED)])
      .Because("a released or cleared pair drops to zero; a stale count would say it still holds history back");
  }

  [Test]
  public async Task BlockedEvents_BeforeAnyReport_ReportsNothingAsync() {
    var (_, factory) = _newMetrics();

    await Assert.That(_observe(factory)).IsEmpty();
  }

  [Test]
  public async Task ClaimStartupSummary_IsTrueOnce_PerProcessAsync() {
    var (metrics, _) = _newMetrics();

    await Assert.That(metrics.ClaimStartupSummary()).IsTrue();
    await Assert.That(metrics.ClaimStartupSummary()).IsFalse()
      .Because("the summary is logged at startup, not on every maintenance cycle");
  }

  [Test]
  public async Task Arguments_AreCheckedAsync() {
    var (metrics, _) = _newMetrics();

    await Assert.That(() => new EphemeralBlockerMetrics(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => metrics.Publish(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheMeter_IsInTheCanonicalListAsync() {
    await Assert.That(WhizbangMeters.All).Contains(EphemeralBlockerMetrics.METER_NAME);
  }
}
