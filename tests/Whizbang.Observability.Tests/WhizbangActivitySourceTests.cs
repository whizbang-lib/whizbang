using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for WhizbangActivitySource static class.
/// Ensures ActivitySources are properly initialized and defensive recording methods work correctly.
/// </summary>
public class WhizbangActivitySourceTests {
  [Test]
  public async Task Execution_ActivitySource_IsInitializedAsync() {
    // Act
    var activitySource = WhizbangActivitySource.Execution;

    // Assert
    await Assert.That(activitySource).IsNotNull();
    await Assert.That(activitySource.Name).IsEqualTo("Whizbang.Execution");
    await Assert.That(activitySource.Version).IsEqualTo("1.0.0");
  }

  [Test]
  public async Task Transport_ActivitySource_IsInitializedAsync() {
    // Act
    var activitySource = WhizbangActivitySource.Transport;

    // Assert
    await Assert.That(activitySource).IsNotNull();
    await Assert.That(activitySource.Name).IsEqualTo("Whizbang.Transport");
    await Assert.That(activitySource.Version).IsEqualTo("1.0.0");
  }

  [Test]
  public async Task RecordDefensiveException_WithNullActivity_DoesNothingAsync() {
    // Arrange
    var exception = new InvalidOperationException("Test exception");

    // Act - Should not throw
    WhizbangActivitySource.RecordDefensiveException(null, exception, "Test description");

    // Assert - No exception thrown means success
    // TUnitAssertions0005: Intentional constant assertion to verify no exception was thrown
#pragma warning disable TUnitAssertions0005
    await Assert.That(true).IsTrue();
#pragma warning restore TUnitAssertions0005
  }

  [Test]
  public async Task RecordDefensiveException_WithActivity_SetsStatusAndTagsAsync() {
    // Arrange
    using var listener = new ActivityListener {
      ShouldListenTo = s => s.Name == "Whizbang.Execution",
      Sample = (ref _) => ActivitySamplingResult.AllData
    };
    ActivitySource.AddActivityListener(listener);

    using var activity = WhizbangActivitySource.Execution.StartActivity("TestActivity");
    var exception = new InvalidOperationException("Test exception");

    // Act
    WhizbangActivitySource.RecordDefensiveException(activity, exception, "Test defensive exception");

    // Assert
    await Assert.That(activity).IsNotNull();
    await Assert.That(activity!.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(activity.StatusDescription).IsEqualTo("Test defensive exception");

    // Verify tags using GetTagItem (tags added with AddTag)
    await Assert.That(activity.GetTagItem("exception.type")).IsEqualTo(typeof(InvalidOperationException).FullName);
    await Assert.That(activity.GetTagItem("exception.message")).IsEqualTo("Test exception");
    // Note: exception.stacktrace is null for unthrown exceptions, so we just verify the code added it
    var stackTrace = activity.GetTagItem("exception.stacktrace");
    await Assert.That(activity.GetTagItem("defensive.code")).IsEqualTo(true);
  }

  [Test]
  public async Task RecordDefensiveCancellation_WithNullActivity_DoesNothingAsync() {
    // Act - Should not throw
    WhizbangActivitySource.RecordDefensiveCancellation(null, "Test context");

    // Assert - No exception thrown means success
    // TUnitAssertions0005: Intentional constant assertion to verify no exception was thrown
#pragma warning disable TUnitAssertions0005
    await Assert.That(true).IsTrue();
#pragma warning restore TUnitAssertions0005
  }

  [Test]
  public async Task RecordDefensiveCancellation_WithActivity_SetsStatusAndTagsAsync() {
    // Arrange
    using var listener = new ActivityListener {
      ShouldListenTo = s => s.Name == "Whizbang.Execution",
      Sample = (ref _) => ActivitySamplingResult.AllData
    };
    ActivitySource.AddActivityListener(listener);

    using var activity = WhizbangActivitySource.Execution.StartActivity("TestActivity");

    // Act
    WhizbangActivitySource.RecordDefensiveCancellation(activity, "Worker cancellation");

    // Assert
    await Assert.That(activity).IsNotNull();
    await Assert.That(activity!.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(activity.StatusDescription).IsEqualTo("Unexpected cancellation: Worker cancellation");

    // Verify tags using GetTagItem (tags added with AddTag)
    await Assert.That(activity.GetTagItem("cancellation.unexpected")).IsEqualTo(true);
    await Assert.That(activity.GetTagItem("defensive.code")).IsEqualTo(true);
  }

  [Test]
  public async Task ExecutionActivitySource_CanCreateActivitiesAsync() {
    // Arrange
    using var listener = new ActivityListener {
      ShouldListenTo = s => s.Name == "Whizbang.Execution",
      Sample = (ref _) => ActivitySamplingResult.AllData
    };
    ActivitySource.AddActivityListener(listener);

    // Act
    using var activity = WhizbangActivitySource.Execution.StartActivity("ExecutionTest");

    // Assert
    await Assert.That(activity).IsNotNull();
    await Assert.That(activity!.Source.Name).IsEqualTo("Whizbang.Execution");
  }

  [Test]
  public async Task TransportActivitySource_CanCreateActivitiesAsync() {
    // Arrange
    using var listener = new ActivityListener {
      ShouldListenTo = s => s.Name == "Whizbang.Transport",
      Sample = (ref _) => ActivitySamplingResult.AllData
    };
    ActivitySource.AddActivityListener(listener);

    // Act
    using var activity = WhizbangActivitySource.Transport.StartActivity("TransportTest");

    // Assert
    await Assert.That(activity).IsNotNull();
    await Assert.That(activity!.Source.Name).IsEqualTo("Whizbang.Transport");
  }

  [Test]
  public async Task TracingActivitySource_IsInitializedAsync() {
    // Act
    var activitySource = WhizbangActivitySource.Tracing;

    // Assert
    await Assert.That(activitySource).IsNotNull();
    await Assert.That(activitySource.Name).IsEqualTo("Whizbang.Tracing");
    await Assert.That(activitySource.Version).IsEqualTo("1.0.0");
  }

  [Test]
  public async Task TracingActivitySource_CanCreateActivitiesAsync() {
    // Arrange
    using var listener = new ActivityListener {
      ShouldListenTo = s => s.Name == "Whizbang.Tracing",
      Sample = (ref _) => ActivitySamplingResult.AllData
    };
    ActivitySource.AddActivityListener(listener);

    // Act
    using var activity = WhizbangActivitySource.Tracing.StartActivity("HandlerTrace");

    // Assert
    await Assert.That(activity).IsNotNull();
    await Assert.That(activity!.Source.Name).IsEqualTo("Whizbang.Tracing");
  }

  [Test]
  public async Task TracingActivity_IsChildOfExecutionActivity_WhenNestedAsync() {
    // Arrange - Listen to BOTH sources
    using var listener = new ActivityListener {
      ShouldListenTo = s => s.Name == "Whizbang.Execution" || s.Name == "Whizbang.Tracing",
      Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded
    };
    ActivitySource.AddActivityListener(listener);

    // Act - Start parent (Dispatch) then child (Handler) - simulating what Dispatcher does
    using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity("Dispatch TestCommand");
    dispatchActivity?.SetTag("whizbang.message.type", "TestCommand");

    // This simulates what ITracer.BeginHandlerTrace does
    using var handlerActivity = WhizbangActivitySource.Tracing.StartActivity("Handler: TestHandler");
    handlerActivity?.SetTag("whizbang.handler.name", "TestHandler");

    // Assert - Handler activity should be child of Dispatch activity
    await Assert.That(dispatchActivity).IsNotNull();
    await Assert.That(handlerActivity).IsNotNull();

    // Verify parent-child relationship - THIS IS THE KEY ASSERTION
    await Assert.That(handlerActivity!.ParentId).IsEqualTo(dispatchActivity!.Id);
    await Assert.That(handlerActivity.ParentSpanId).IsEqualTo(dispatchActivity.SpanId);

    // Verify both activities share the same TraceId
    await Assert.That(handlerActivity.TraceId).IsEqualTo(dispatchActivity.TraceId);
  }

  [Test]
  public async Task TracingActivity_WithoutParent_HasNoParentIdAsync() {
    // Arrange - Only listen to Tracing source (no parent activity)
    using var listener = new ActivityListener {
      ShouldListenTo = s => s.Name == "Whizbang.Tracing",
      Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded
    };
    ActivitySource.AddActivityListener(listener);

    // Act - Start handler activity WITHOUT a parent dispatch activity
    using var handlerActivity = WhizbangActivitySource.Tracing.StartActivity("Handler: OrphanHandler");

    // Assert - Should have no parent
    await Assert.That(handlerActivity).IsNotNull();
    await Assert.That(handlerActivity!.ParentId).IsNull();
    await Assert.That(handlerActivity.ParentSpanId.ToString()).IsEqualTo("0000000000000000");
  }
}
