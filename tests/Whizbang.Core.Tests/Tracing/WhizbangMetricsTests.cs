using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for WhizbangMetrics which provides static metric instruments.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/WhizbangMetrics.cs</code-under-test>
public class WhizbangMetricsTests {
  #region Meter Configuration

  [Test]
  public async Task MeterName_IsWhizbangAsync() {
    // Arrange
    var meterName = WhizbangMetrics.METER_NAME;

    // Assert
    await Assert.That(meterName).IsEqualTo("Whizbang");
  }

  #endregion

  #region Handler Metrics Existence

  [Test]
  public async Task HandlerInvocations_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.HandlerInvocations).IsNotNull();
  }

  [Test]
  public async Task HandlerSuccesses_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.HandlerSuccesses).IsNotNull();
  }

  [Test]
  public async Task HandlerFailures_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.HandlerFailures).IsNotNull();
  }

  [Test]
  public async Task HandlerEarlyReturns_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.HandlerEarlyReturns).IsNotNull();
  }

  [Test]
  public async Task HandlerDuration_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.HandlerDuration).IsNotNull();
  }

  [Test]
  public async Task HandlerActive_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.HandlerActive).IsNotNull();
  }

  #endregion

  #region Dispatcher Metrics Existence

  [Test]
  public async Task DispatchTotal_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.DispatchTotal).IsNotNull();
  }

  [Test]
  public async Task DispatchDuration_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.DispatchDuration).IsNotNull();
  }

  [Test]
  public async Task ReceptorDiscovered_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.ReceptorDiscovered).IsNotNull();
  }

  #endregion

  #region Message Metrics Existence

  [Test]
  public async Task MessagesDispatched_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.MessagesDispatched).IsNotNull();
  }

  [Test]
  public async Task MessagesReceived_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.MessagesReceived).IsNotNull();
  }

  [Test]
  public async Task MessagesProcessingTime_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.MessagesProcessingTime).IsNotNull();
  }

  #endregion

  #region Event Metrics Existence

  [Test]
  public async Task EventsStored_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.EventsStored).IsNotNull();
  }

  [Test]
  public async Task EventsPublished_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.EventsPublished).IsNotNull();
  }

  #endregion

  #region Outbox Metrics Existence

  [Test]
  public async Task OutboxWrites_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.OutboxWrites).IsNotNull();
  }

  [Test]
  public async Task OutboxPending_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.OutboxPending).IsNotNull();
  }

  [Test]
  public async Task OutboxBatchSize_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.OutboxBatchSize).IsNotNull();
  }

  [Test]
  public async Task OutboxDeliveryLatency_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.OutboxDeliveryLatency).IsNotNull();
  }

  #endregion

  #region Inbox Metrics Existence

  [Test]
  public async Task InboxReceived_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.InboxReceived).IsNotNull();
  }

  [Test]
  public async Task InboxPending_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.InboxPending).IsNotNull();
  }

  [Test]
  public async Task InboxBatchSize_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.InboxBatchSize).IsNotNull();
  }

  [Test]
  public async Task InboxProcessingTime_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.InboxProcessingTime).IsNotNull();
  }

  [Test]
  public async Task InboxDuplicates_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.InboxDuplicates).IsNotNull();
  }

  #endregion

  #region EventStore Metrics Existence

  [Test]
  public async Task EventStoreAppends_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.EventStoreAppends).IsNotNull();
  }

  [Test]
  public async Task EventStoreReads_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.EventStoreReads).IsNotNull();
  }

  [Test]
  public async Task EventStoreEventsPerAppend_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.EventStoreEventsPerAppend).IsNotNull();
  }

  [Test]
  public async Task EventStoreReadLatency_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.EventStoreReadLatency).IsNotNull();
  }

  [Test]
  public async Task EventStoreWriteLatency_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.EventStoreWriteLatency).IsNotNull();
  }

  #endregion

  #region Lifecycle Metrics Existence

  [Test]
  public async Task LifecycleInvocations_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.LifecycleInvocations).IsNotNull();
  }

  [Test]
  public async Task LifecycleDuration_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.LifecycleDuration).IsNotNull();
  }

  [Test]
  public async Task LifecycleSkipped_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.LifecycleSkipped).IsNotNull();
  }

  #endregion

  #region Perspective Metrics Existence

  [Test]
  public async Task PerspectiveUpdates_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.PerspectiveUpdates).IsNotNull();
  }

  [Test]
  public async Task PerspectiveDuration_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.PerspectiveDuration).IsNotNull();
  }

  [Test]
  public async Task PerspectiveLag_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.PerspectiveLag).IsNotNull();
  }

  [Test]
  public async Task PerspectiveErrors_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.PerspectiveErrors).IsNotNull();
  }

  #endregion

  #region Worker Metrics Existence

  [Test]
  public async Task WorkerIterations_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.WorkerIterations).IsNotNull();
  }

  [Test]
  public async Task WorkerIdleTime_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.WorkerIdleTime).IsNotNull();
  }

  [Test]
  public async Task WorkerActive_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.WorkerActive).IsNotNull();
  }

  [Test]
  public async Task WorkerErrors_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.WorkerErrors).IsNotNull();
  }

  #endregion

  #region Error Metrics Existence

  [Test]
  public async Task ErrorsTotal_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.ErrorsTotal).IsNotNull();
  }

  [Test]
  public async Task ErrorsUnhandled_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.ErrorsUnhandled).IsNotNull();
  }

  #endregion

  #region Policy Metrics Existence

  [Test]
  public async Task PolicyCircuitBreaks_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.PolicyCircuitBreaks).IsNotNull();
  }

  [Test]
  public async Task PolicyRetries_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.PolicyRetries).IsNotNull();
  }

  #endregion

  #region Security Metrics Existence

  [Test]
  public async Task SecurityContextPropagations_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.SecurityContextPropagations).IsNotNull();
  }

  [Test]
  public async Task SecurityMissingContext_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.SecurityMissingContext).IsNotNull();
  }

  #endregion

  #region Tags Metrics Existence

  [Test]
  public async Task TagsProcessed_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.TagsProcessed).IsNotNull();
  }

  [Test]
  public async Task TagsDuration_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.TagsDuration).IsNotNull();
  }

  [Test]
  public async Task TagsErrors_ExistsAsync() {
    // Assert
    await Assert.That(WhizbangMetrics.TagsErrors).IsNotNull();
  }

  #endregion
}
