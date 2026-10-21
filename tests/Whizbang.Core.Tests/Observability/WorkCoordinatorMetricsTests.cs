// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for WorkCoordinatorMetrics instrument creation and recording.
/// Uses MeterListener (AOT-safe) via MetricAssertionHelper.
/// </summary>
/// <docs>observability/metrics</docs>
[Category("Core")]
[Category("Observability")]
public class WorkCoordinatorMetricsTests {
  [Test]
  public async Task WCMetrics_MeterName_IsWhizbangWorkCoordinatorAsync() {
    string meterName = WorkCoordinatorMetrics.METER_NAME;
    await Assert.That(meterName).IsEqualTo("Whizbang.WorkCoordinator");
  }

  [Test]
  public async Task WCMetrics_Constructor_CreatesAllInstrumentsAsync() {
    // Arrange & Act
    var whizbangMetrics = new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>());
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);

    // Assert - all properties are non-null
    await Assert.That(metrics.FlushDuration).IsNotNull();
    await Assert.That(metrics.CommitHandlerFallbacks).IsNotNull();
    await Assert.That(metrics.OutboxEmissionDeduplicated).IsNotNull();
    await Assert.That(metrics.FlushCalls).IsNotNull();
    await Assert.That(metrics.EmptyFlushCalls).IsNotNull();
    await Assert.That(metrics.PublisherLeaseRenewals).IsNotNull();
    await Assert.That(metrics.PublisherBufferedMessages).IsNotNull();
    await Assert.That(metrics.MaintenanceTaskDuration).IsNotNull();
    await Assert.That(metrics.MaintenanceTaskRowsAffected).IsNotNull();
  }

  [Test]
  public async Task WCMetrics_PublishesNoInstrumentOfTheRemovedWorkBatchCallAsync() {
    using var factory = new TestMeterFactory();
    _ = new WorkCoordinatorMetrics(new WhizbangMetrics(factory));
    var published = new List<string>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, _) => {
        if (instrument.Meter.Name == WorkCoordinatorMetrics.METER_NAME) {
          published.Add(instrument.Name);
        }
      }
    };

    listener.Start();

    await Assert.That(published).IsNotEmpty();
    await Assert.That(published.Where(n =>
        n.Contains(".process_batch.", StringComparison.Ordinal)
        || n.Contains(".work_coordinator.batch.", StringComparison.Ordinal)
        || n.Contains(".work_coordinator.returned.", StringComparison.Ordinal))).IsEmpty()
      .Because("#1535: the work-batch SQL call these timed, counted and sized is gone, so the instruments are retired");
  }

  [Test]
  public async Task WCMetrics_Constructor_WithMeterFactory_UsesMeterFactoryAsync() {
    // Arrange
    var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);

    // Act
    _ = new WorkCoordinatorMetrics(whizbangMetrics);

    // Assert - factory was used (meter was created)
    await Assert.That(factory.CreatedMeterNames).Contains(WorkCoordinatorMetrics.METER_NAME);
  }

  [Test]
  public async Task WCMetrics_OutboxEmissionDeduplicated_CountsPerMessageTypeAsync() {
    // Arrange - a retry re-emitted two events of one type and one of another; the store skipped all three
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.OutboxEmissionDeduplicated.Add(1, new KeyValuePair<string, object?>("message_type", "Orders.OrderPlaced"));
    metrics.OutboxEmissionDeduplicated.Add(1, new KeyValuePair<string, object?>("message_type", "Orders.OrderPlaced"));
    metrics.OutboxEmissionDeduplicated.Add(1, new KeyValuePair<string, object?>("message_type", "Orders.OrderPriced"));

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.outbox.emission_deduplicated");
    var placed = measurements.Where(m => m.Tags.GetValueOrDefault("message_type") == "Orders.OrderPlaced").ToList();
    var priced = measurements.Where(m => m.Tags.GetValueOrDefault("message_type") == "Orders.OrderPriced").ToList();
    await Assert.That(placed).Count().IsEqualTo(1);
    await Assert.That(placed[0].Value).IsEqualTo(2);
    await Assert.That(priced).Count().IsEqualTo(1);
    await Assert.That(priced[0].Value).IsEqualTo(1);
  }

  [Test]
  public async Task WCMetrics_FlushDuration_RecordedPerFlushAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.FlushDuration.Record(25.3);

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.flush.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(25.3);
  }

  [Test]
  public async Task WCMetrics_FlushCalls_IncrementedPerFlushAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.FlushCalls.Add(1);

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.flush.calls");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task WCMetrics_EmptyFlushCalls_IncrementedWhenNoQueuedWorkAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.EmptyFlushCalls.Add(1);

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.flush.empty_calls");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task WCMetrics_PublisherLeaseRenewals_IncrementedAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.PublisherLeaseRenewals.Add(1);

    // Assert
    var measurements = helper.GetByName("whizbang.publisher.lease_renewals");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task WCMetrics_PublisherBufferedMessages_IncrementedAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.PublisherBufferedMessages.Add(10);

    // Assert
    var measurements = helper.GetByName("whizbang.publisher.buffered_messages");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(10);
  }

  [Test]
  public async Task WCMetrics_MaintenanceTaskDuration_RecordedPerTaskAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.MaintenanceTaskDuration.Record(500.0, new KeyValuePair<string, object?>("task_name", "purge_completed_outbox"));

    // Assert
    var measurements = helper.GetByName("whizbang.maintenance.task.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Tags["task_name"]).IsEqualTo("purge_completed_outbox");
  }

  [Test]
  public async Task WCMetrics_MaintenanceTaskRowsAffected_IncludesTaskNameTagAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.MaintenanceTaskRowsAffected.Record(1500, new KeyValuePair<string, object?>("task_name", "purge_completed_inbox"));

    // Assert
    var measurements = helper.GetByName("whizbang.maintenance.task.rows_affected");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(1500);
    await Assert.That(measurements[0].Tags["task_name"]).IsEqualTo("purge_completed_inbox");
  }
}
