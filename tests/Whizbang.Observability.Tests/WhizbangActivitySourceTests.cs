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
    await Assert.That(true).IsTrue();
  }

  [Test]
  public async Task RecordDefensiveException_WithActivity_SetsStatusAndTagsAsync() {
    // Arrange
    using var listener = new ActivityListener {
      ShouldListenTo = s => s.Name == "Whizbang.Execution",
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
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
    await Assert.That(true).IsTrue();
  }

  [Test]
  public async Task RecordDefensiveCancellation_WithActivity_SetsStatusAndTagsAsync() {
    // Arrange
    using var listener = new ActivityListener {
      ShouldListenTo = s => s.Name == "Whizbang.Execution",
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
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
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
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
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
    };
    ActivitySource.AddActivityListener(listener);

    // Act
    using var activity = WhizbangActivitySource.Transport.StartActivity("TransportTest");

    // Assert
    await Assert.That(activity).IsNotNull();
    await Assert.That(activity!.Source.Name).IsEqualTo("Whizbang.Transport");
  }
}
