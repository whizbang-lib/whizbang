// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The event-type gauges: which event types the event store's bytes belong to, and how much of each event is
/// payload and how much metadata.
/// </summary>
/// <remarks>
/// Read the way an exporter reads them, through a listener on this test's own meter, so what is asserted is
/// what a dashboard would receive.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsMetrics.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Observability/ITableStatisticsProvider.cs</code-under-test>
/// <docs>operations/observability/metrics#event-type-bytes</docs>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsMetricsEventTypeTests {
  private const string ORDER_PLACED = "Shop.OrderPlaced, Shop";
  private const string EMBEDDING_CREATED = "Search.EmbeddingCreated, Search";

  [Test]
  public async Task ASample_ReportsMeanBytesPerPartAndTheEstimatedTotalPerTypeAsync() {
    using var gauges = new Gauges();

    gauges.Metrics.UpdateEventTypeBytes(new Dictionary<string, EventTypeBytes> {
      [ORDER_PLACED] = new(SampledRows: 40, MeanDataBytes: 310.5, MeanMetadataBytes: 612.25, EstimatedTotalBytes: 3_692_000),
      [EMBEDDING_CREATED] = new(SampledRows: 3, MeanDataBytes: 24_576, MeanMetadataBytes: 590, EstimatedTotalBytes: 7_549_800),
    });
    var readings = gauges.Read();

    await Assert.That(readings).Contains(("whizbang.event_store.sampled_bytes_per_event", 310.5, ORDER_PLACED, "data"));
    await Assert.That(readings).Contains(("whizbang.event_store.sampled_bytes_per_event", 612.25, ORDER_PLACED, "metadata"))
      .Because("metadata larger than the payload is the finding slimming exists for, and only the split shows it");
    await Assert.That(readings).Contains(("whizbang.event_store.sampled_bytes_per_event", 24_576d, EMBEDDING_CREATED, "data"));
    await Assert.That(readings).Contains(("whizbang.event_store.sampled_bytes_per_event", 590d, EMBEDDING_CREATED, "metadata"));
    await Assert.That(readings).Contains(("whizbang.event_store.estimated_type_bytes", 3_692_000d, ORDER_PLACED, (string?)null));
    await Assert.That(readings).Contains(("whizbang.event_store.estimated_type_bytes", 7_549_800d, EMBEDDING_CREATED, (string?)null))
      .Because("a rare type with large events can outweigh a frequent one, which is why the total is per type");
    await Assert.That(readings.Count).IsEqualTo(6)
      .Because("each type reports two parts and one total, which bounds the series at three per type");
  }

  [Test]
  public async Task ALaterSample_ReplacesTheEarlierOne_AndAnEmptyOneClearsItAsync() {
    using var gauges = new Gauges();
    var order = new EventTypeBytes(SampledRows: 1, MeanDataBytes: 100, MeanMetadataBytes: 200, EstimatedTotalBytes: 30_000);

    gauges.Metrics.UpdateEventTypeBytes(new Dictionary<string, EventTypeBytes> { [ORDER_PLACED] = order, [EMBEDDING_CREATED] = order });
    gauges.Metrics.UpdateEventTypeBytes(new Dictionary<string, EventTypeBytes> { [ORDER_PLACED] = order with { MeanDataBytes = 150 } });
    var replaced = gauges.Read();
    gauges.Metrics.UpdateEventTypeBytes(new Dictionary<string, EventTypeBytes>());
    var cleared = gauges.Read();

    await Assert.That(replaced.Any(r => r.EventType == EMBEDDING_CREATED)).IsFalse()
      .Because("a type absent from the latest sample is no longer reported, rather than kept at its old value");
    await Assert.That(replaced).Contains(("whizbang.event_store.sampled_bytes_per_event", 150d, ORDER_PLACED, "data"));
    await Assert.That(cleared).IsEmpty()
      .Because("an instance that did not take the window clears its reading, so one instance reports at a time");
  }

  [Test]
  [Arguments(100d, 1_234L, 1_234L)]
  [Arguments(1d, 1_234L, 123_400L)]
  [Arguments(2.5d, 1_000L, 40_000L)]
  [Arguments(3d, 100L, 3_333L)]
  public async Task Extrapolate_ScalesTheSampledBytesByTheShareReadAsync(double percent, long sampled, long expected) {
    var request = new EventTypeSampleRequest("window", percent, 50);

    await Assert.That(request.Extrapolate(sampled)).IsEqualTo(expected)
      .Because("each page is read with the same probability, so the sample is the table's bytes times that share");
  }

  private sealed class Gauges : IDisposable {
    private readonly TestMeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, double Value, string? EventType, string? Part)> _readings = [];

    public Gauges() {
      Metrics = new TableStatisticsMetrics(new WhizbangMetrics(_factory));
      var meter = _factory.CreatedMeters[0];
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter == meter && instrument.Name.StartsWith("whizbang.event_store.", StringComparison.Ordinal)) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _record(instrument, value, tags));
      _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => _record(instrument, value, tags));
      _listener.Start();
    }

    public TableStatisticsMetrics Metrics { get; }

    public List<(string Name, double Value, string? EventType, string? Part)> Read() {
      _readings.Clear();
      _listener.RecordObservableInstruments();
      return [.. _readings];
    }

    private void _record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      string? eventType = null;
      string? part = null;
      foreach (var tag in tags) {
        if (tag.Key == "event_type") {
          eventType = tag.Value?.ToString();
        } else if (tag.Key == "part") {
          part = tag.Value?.ToString();
        }
      }
      _readings.Add((instrument.Name, value, eventType, part));
    }

    public void Dispose() {
      _listener.Dispose();
      _factory.Dispose();
    }
  }
}
