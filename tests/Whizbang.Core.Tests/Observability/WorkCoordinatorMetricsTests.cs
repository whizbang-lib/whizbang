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
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);

    // Assert - all properties are non-null
    await Assert.That(metrics.ProcessBatchDuration).IsNotNull();
    await Assert.That(metrics.FlushDuration).IsNotNull();
    await Assert.That(metrics.BatchOutboxMessages).IsNotNull();
    await Assert.That(metrics.BatchInboxMessages).IsNotNull();
    await Assert.That(metrics.BatchCompletions).IsNotNull();
    await Assert.That(metrics.BatchFailures).IsNotNull();
    await Assert.That(metrics.ReturnedOutboxWork).IsNotNull();
    await Assert.That(metrics.ReturnedInboxWork).IsNotNull();
    await Assert.That(metrics.ReturnedPerspectiveWork).IsNotNull();
    await Assert.That(metrics.ProcessBatchCalls).IsNotNull();
    await Assert.That(metrics.ProcessBatchErrors).IsNotNull();
    await Assert.That(metrics.FlushCalls).IsNotNull();
    await Assert.That(metrics.EmptyFlushCalls).IsNotNull();
    await Assert.That(metrics.PublisherLeaseRenewals).IsNotNull();
    await Assert.That(metrics.PublisherBufferedMessages).IsNotNull();
    await Assert.That(metrics.MaintenanceTaskDuration).IsNotNull();
    await Assert.That(metrics.MaintenanceTaskRowsAffected).IsNotNull();
  }

  [Test]
  public async Task WCMetrics_Constructor_WithMeterFactory_UsesMeterFactoryAsync() {
    // Arrange
    var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);

    // Act
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);

    // Assert - factory was used (meter was created)
    await Assert.That(factory.CreatedMeterNames).Contains(WorkCoordinatorMetrics.METER_NAME);
  }

  [Test]
  public async Task WCMetrics_ProcessBatchDuration_RecordedOnSuccessAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.ProcessBatchDuration.Record(150.5);

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.process_batch.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(150.5);
  }

  [Test]
  public async Task WCMetrics_ProcessBatchDuration_IncludesStrategyTagAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.ProcessBatchDuration.Record(50.0, new KeyValuePair<string, object?>("strategy", "scoped"));

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.process_batch.duration");
    await Assert.That(measurements[0].Tags["strategy"]).IsEqualTo("scoped");
  }

  [Test]
  public async Task WCMetrics_ProcessBatchCalls_IncrementedPerCallAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.ProcessBatchCalls.Add(1);
    metrics.ProcessBatchCalls.Add(1);

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.process_batch.calls");
    await Assert.That(measurements).Count().IsEqualTo(2);
  }

  [Test]
  public async Task WCMetrics_ProcessBatchErrors_IncrementedOnExceptionAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.ProcessBatchErrors.Add(1, new KeyValuePair<string, object?>("error_type", "NpgsqlException"));

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.process_batch.errors");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Tags["error_type"]).IsEqualTo("NpgsqlException");
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
  public async Task WCMetrics_BatchOutboxMessageCount_RecordsCorrectCountAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.BatchOutboxMessages.Record(5);

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.batch.outbox_messages");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(5);
  }

  [Test]
  public async Task WCMetrics_BatchInboxMessageCount_RecordsCorrectCountAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.BatchInboxMessages.Record(3);

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.batch.inbox_messages");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(3);
  }

  [Test]
  public async Task WCMetrics_BatchCompletionCount_RecordsTotalCompletionsAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.BatchCompletions.Record(7);

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.batch.completions");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(7);
  }

  [Test]
  public async Task WCMetrics_BatchFailureCount_RecordsTotalFailuresAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.BatchFailures.Record(2);

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.batch.failures");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(2);
  }

  [Test]
  public async Task WCMetrics_ReturnedOutboxWorkCount_RecordsCorrectCountAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.ReturnedOutboxWork.Record(4);

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.returned.outbox_work");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(4);
  }

  [Test]
  public async Task WCMetrics_ReturnedInboxWorkCount_RecordsCorrectCountAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.ReturnedInboxWork.Record(6);

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.returned.inbox_work");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(6);
  }

  [Test]
  public async Task WCMetrics_ReturnedPerspectiveWorkCount_RecordsCorrectCountAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.ReturnedPerspectiveWork.Record(1);

    // Assert
    var measurements = helper.GetByName("whizbang.work_coordinator.returned.perspective_work");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(1);
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
