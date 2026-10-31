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
/// payload, metadata, event-store pointer row and outbox metadata.
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

  private const string SAMPLED = "whizbang.event_store.sampled_bytes_per_event";
  private const string ESTIMATED = "whizbang.event_store.estimated_type_bytes";

  [Test]
  public async Task ASample_ReportsEveryPartOnBothGauges_UnderItsPartTagAsync() {
    using var gauges = new Gauges();

    gauges.Metrics.UpdateEventTypeBytes(new Dictionary<string, EventTypeBytes> {
      [ORDER_PLACED] = new(
        Data: new(40, 310.5, 1_242_000),
        Metadata: new(40, 612.25, 2_449_000),
        PointerRow: new(52, 180, 936_000),
        OutboxMetadata: new(7, 640, 448_000)),
    });
    var readings = gauges.Read();

    await Assert.That(readings).Contains((SAMPLED, 310.5, ORDER_PLACED, EventStoreBytesParts.DATA));
    await Assert.That(readings).Contains((SAMPLED, 612.25, ORDER_PLACED, EventStoreBytesParts.METADATA))
      .Because("metadata larger than the payload is the finding slimming exists for, and only the split shows it");
    await Assert.That(readings).Contains((SAMPLED, 180d, ORDER_PLACED, EventStoreBytesParts.POINTER_ROW))
      .Because("the event-store row is paid for every event, body or not, so it is a part of its own");
    await Assert.That(readings).Contains((SAMPLED, 640d, ORDER_PLACED, EventStoreBytesParts.OUTBOX_METADATA))
      .Because("the outbox carries the metadata a second time until the row is purged, and only its own part shows that");
    await Assert.That(readings).Contains((ESTIMATED, 1_242_000d, ORDER_PLACED, EventStoreBytesParts.DATA));
    await Assert.That(readings).Contains((ESTIMATED, 2_449_000d, ORDER_PLACED, EventStoreBytesParts.METADATA));
    await Assert.That(readings).Contains((ESTIMATED, 936_000d, ORDER_PLACED, EventStoreBytesParts.POINTER_ROW));
    await Assert.That(readings).Contains((ESTIMATED, 448_000d, ORDER_PLACED, EventStoreBytesParts.OUTBOX_METADATA))
      .Because("the total is split by part too, so a sum by event_type is the type's whole footprint and a sum by part is where it sits");
    await Assert.That(readings.Count).IsEqualTo(8)
      .Because("each type reports at most four parts on each of the two gauges");
  }

  [Test]
  public async Task APartTheSampleReadNoRowsOf_IsNotReported_WhileOneItReadIsAsync() {
    using var gauges = new Gauges();

    gauges.Metrics.UpdateEventTypeBytes(new Dictionary<string, EventTypeBytes> {
      // Bodies reaped and outbox rows purged: only the pointer rows are left.
      [EMBEDDING_CREATED] = new(Data: default, Metadata: default, PointerRow: new(3, 170, 51_000), OutboxMetadata: default),
      // The control: the same shape with one outbox row read reports that part.
      [ORDER_PLACED] = new(Data: default, Metadata: default, PointerRow: new(3, 170, 51_000), OutboxMetadata: new(1, 600, 60_000)),
    });
    var readings = gauges.Read();

    await Assert.That(readings.Where(r => r.EventType == EMBEDDING_CREATED).Select(r => r.Part).Distinct().ToList())
      .IsEquivalentTo((List<string?>)[EventStoreBytesParts.POINTER_ROW])
      .Because("a part with no rows read has no mean; reporting zero would claim a measurement that was not made");
    await Assert.That(readings).Contains((SAMPLED, 600d, ORDER_PLACED, EventStoreBytesParts.OUTBOX_METADATA))
      .Because("one row read is a measurement, so the part is reported");
    await Assert.That(readings).Contains((ESTIMATED, 60_000d, ORDER_PLACED, EventStoreBytesParts.OUTBOX_METADATA));
  }

  [Test]
  public async Task ALaterSample_ReplacesTheEarlierOne_AndAnEmptyOneClearsItAsync() {
    using var gauges = new Gauges();
    var order = new EventTypeBytes(
      Data: new(1, 100, 10_000), Metadata: new(1, 200, 20_000), PointerRow: default, OutboxMetadata: default);

    gauges.Metrics.UpdateEventTypeBytes(new Dictionary<string, EventTypeBytes> { [ORDER_PLACED] = order, [EMBEDDING_CREATED] = order });
    gauges.Metrics.UpdateEventTypeBytes(new Dictionary<string, EventTypeBytes> {
      [ORDER_PLACED] = order with { Data = new(1, 150, 15_000) },
    });
    var replaced = gauges.Read();
    gauges.Metrics.UpdateEventTypeBytes(new Dictionary<string, EventTypeBytes>());
    var cleared = gauges.Read();

    await Assert.That(replaced.Any(r => r.EventType == EMBEDDING_CREATED)).IsFalse()
      .Because("a type absent from the latest sample is no longer reported, rather than kept at its old value");
    await Assert.That(replaced).Contains((SAMPLED, 150d, ORDER_PLACED, EventStoreBytesParts.DATA));
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
