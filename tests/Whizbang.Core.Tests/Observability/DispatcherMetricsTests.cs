using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for DispatcherMetrics instrument creation and recording.
/// </summary>
/// <docs>observability/metrics</docs>
[Category("Core")]
[Category("Observability")]
public class DispatcherMetricsTests {
  [Test]
  public async Task DispatcherMetrics_MeterName_IsWhizbangDispatcherAsync() {
    string meterName = DispatcherMetrics.METER_NAME;
    await Assert.That(meterName).IsEqualTo("Whizbang.Dispatcher");
  }

  [Test]
  public async Task DispatcherMetrics_Constructor_CreatesAllInstrumentsAsync() {
    // Arrange & Act
    var metrics = new DispatcherMetrics(new WhizbangMetrics());

    // Assert
    await Assert.That(metrics.SendDuration).IsNotNull();
    await Assert.That(metrics.PublishDuration).IsNotNull();
    await Assert.That(metrics.LocalInvokeDuration).IsNotNull();
    await Assert.That(metrics.LocalInvokeAndSyncDuration).IsNotNull();
    await Assert.That(metrics.CascadeDuration).IsNotNull();
    await Assert.That(metrics.SendManyDuration).IsNotNull();
    await Assert.That(metrics.ReceptorDuration).IsNotNull();
    await Assert.That(metrics.CascadeExtractionDuration).IsNotNull();
    await Assert.That(metrics.PerspectiveSyncDuration).IsNotNull();
    await Assert.That(metrics.PerspectiveWaitDuration).IsNotNull();
    await Assert.That(metrics.SerializationDuration).IsNotNull();
    await Assert.That(metrics.TagProcessingDuration).IsNotNull();
    await Assert.That(metrics.MessagesDispatched).IsNotNull();
    await Assert.That(metrics.EventsCascaded).IsNotNull();
    await Assert.That(metrics.MessagesSerialized).IsNotNull();
    await Assert.That(metrics.DuplicatesDetected).IsNotNull();
    await Assert.That(metrics.PerspectiveSyncTimeouts).IsNotNull();
    await Assert.That(metrics.Errors).IsNotNull();
    await Assert.That(metrics.CascadeEventCount).IsNotNull();
    await Assert.That(metrics.SendManyBatchSize).IsNotNull();
  }

  [Test]
  public async Task DispatcherMetrics_SendDuration_RecordedOnSendAsyncAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.SendDuration.Record(12.5);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.send.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(12.5);
  }

  [Test]
  public async Task DispatcherMetrics_SendDuration_IncludesMessageTypeTagAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.SendDuration.Record(10.0, new KeyValuePair<string, object?>("message_type", "CreateOrder"));

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.send.duration");
    await Assert.That(measurements[0].Tags["message_type"]).IsEqualTo("CreateOrder");
  }

  [Test]
  public async Task DispatcherMetrics_PublishDuration_RecordedOnPublishAsyncAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.PublishDuration.Record(8.0);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.publish.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DispatcherMetrics_LocalInvokeDuration_RecordedOnLocalInvokeAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.LocalInvokeDuration.Record(3.2);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.local_invoke.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DispatcherMetrics_LocalInvokeAndSyncDuration_IncludesPerspectiveWaitAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.LocalInvokeAndSyncDuration.Record(120.0);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.local_invoke_and_sync.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(120.0);
  }

  [Test]
  public async Task DispatcherMetrics_CascadeDuration_RecordedPerCascadeAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.CascadeDuration.Record(15.0);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.cascade.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DispatcherMetrics_ReceptorDuration_RecordedPerReceptorAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.ReceptorDuration.Record(5.0);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.receptor.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DispatcherMetrics_ReceptorDuration_IncludesReceptorIndexTagAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.ReceptorDuration.Record(5.0, new KeyValuePair<string, object?>("receptor_index", "0"));

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.receptor.duration");
    await Assert.That(measurements[0].Tags["receptor_index"]).IsEqualTo("0");
  }

  [Test]
  public async Task DispatcherMetrics_MessagesDispatched_IncrementedAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.MessagesDispatched.Add(1);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.messages_dispatched");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DispatcherMetrics_MessagesDispatched_IncludesPatternTagAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.MessagesDispatched.Add(1, new KeyValuePair<string, object?>("pattern", "send"));

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.messages_dispatched");
    await Assert.That(measurements[0].Tags["pattern"]).IsEqualTo("send");
  }

  [Test]
  public async Task DispatcherMetrics_EventsCascaded_IncrementedWithDestinationTagAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.EventsCascaded.Add(1, new KeyValuePair<string, object?>("destination", "outbox"));

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.events_cascaded");
    await Assert.That(measurements[0].Tags["destination"]).IsEqualTo("outbox");
  }

  [Test]
  public async Task DispatcherMetrics_DuplicatesDetected_IncrementedAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.DuplicatesDetected.Add(1);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.duplicates_detected");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DispatcherMetrics_PerspectiveSyncTimeouts_IncrementedAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.PerspectiveSyncTimeouts.Add(1);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.perspective_sync_timeouts");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DispatcherMetrics_Errors_IncrementedWithErrorTypeTagAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.Errors.Add(1, new KeyValuePair<string, object?>("error_type", "InvalidOperationException"));

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.errors");
    await Assert.That(measurements[0].Tags["error_type"]).IsEqualTo("InvalidOperationException");
  }

  [Test]
  public async Task DispatcherMetrics_CascadeEventCount_RecordsCorrectCountAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.CascadeEventCount.Record(3);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.cascade.event_count");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(3);
  }

  [Test]
  public async Task DispatcherMetrics_SendManyBatchSize_RecordsCorrectCountAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.SendManyBatchSize.Record(25);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.send_many.batch_size");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(25);
  }

  [Test]
  public async Task DispatcherMetrics_SerializationDuration_RecordedAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.SerializationDuration.Record(1.5);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.serialization.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DispatcherMetrics_TagProcessingDuration_RecordedAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.TagProcessingDuration.Record(2.0);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.tag_processing.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DispatcherMetrics_PerspectiveSyncDuration_RecordedAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.PerspectiveSyncDuration.Record(50.0);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.perspective_sync.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DispatcherMetrics_CascadeExtractionDuration_RecordedAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.CascadeExtractionDuration.Record(7.0);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.cascade_extraction.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DispatcherMetrics_PerspectiveWaitDuration_RecordedAsync() {
    // Arrange
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new DispatcherMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    // Act
    metrics.PerspectiveWaitDuration.Record(200.0);

    // Assert
    var measurements = helper.GetByName("whizbang.dispatcher.perspective_wait.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }
}
