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
/// #2092: the paced backfill's gauges report the reading the worker last published: the windows awaiting an answer and
/// each running type's estimated remaining events, tagged by type; a null reading resets them to empty.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/StreamIntegrityMetrics.cs</code-under-test>
public class StreamIntegrityMetricsBackfillGaugeTests {

  [Test]
  public async Task BackfillGauges_ReportTheLastReading_InFlightAndRemainingByTypeAsync() {
    await using var meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterServices.GetRequiredService<IMeterFactory>()));
    var readings = new List<(string Name, long Value, string? Type)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, metrics.BackfillsEnded.Meter) && instrument.Name.StartsWith("whizbang.stream_integrity.backfill_", StringComparison.Ordinal)
          && instrument is not ObservableCounter<long>) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => readings.Add((instrument.Name, value, null)));
    listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
      string? type = null;
      foreach (var tag in tags) {
        if (tag.Key == "event_type") {
          type = tag.Value as string;
        }
      }
      readings.Add((instrument.Name, value, type));
    });
    listener.Start();

    metrics.UpdateBackfillGauges(new BackfillGaugeSnapshot {
      WindowsInFlight = 2,
      Remaining = [new BackfillRemaining("Contracts.A", 400), new BackfillRemaining("Contracts.B", 0)],
    });
    listener.RecordObservableInstruments();

    await Assert.That(readings).Contains(("whizbang.stream_integrity.backfill_windows_in_flight", 2L, (string?)null));
    await Assert.That(readings).Contains(("whizbang.stream_integrity.backfill_remaining_events", 400L, (string?)"Contracts.A"));
    await Assert.That(readings).Contains(("whizbang.stream_integrity.backfill_remaining_events", 0L, (string?)"Contracts.B"));
  }

  [Test]
  public async Task UpdateBackfillGauges_NullReading_ResetsToEmptyAsync() {
    await using var meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterServices.GetRequiredService<IMeterFactory>()));
    metrics.UpdateBackfillGauges(new BackfillGaugeSnapshot { WindowsInFlight = 3 });

    metrics.UpdateBackfillGauges(null!);

    await Assert.That(metrics.CurrentBackfillGaugesForTest).IsSameReferenceAs(BackfillGaugeSnapshot.Empty);
  }
}
