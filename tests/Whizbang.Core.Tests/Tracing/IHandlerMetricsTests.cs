using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for <see cref="IHandlerMetrics"/>.
/// Validates interface contract and usage patterns.
/// </summary>
[Category("Core")]
[Category("Tracing")]
public class IHandlerMetricsTests {

  #region Interface Contract Tests

  [Test]
  public async Task RecordInvocation_AcceptsAllParameters_CorrectlyAsync() {
    // Arrange
    var metrics = new TestHandlerMetrics();

    // Act - call with all parameters
    metrics.RecordInvocation(
        handlerName: "MyApp.Handlers.OrderHandler",
        messageTypeName: "CreateOrder",
        status: HandlerStatus.Success,
        durationMs: 123.45,
        startTimestamp: 1000L,
        endTimestamp: 2000L);

    // Assert - values were recorded
    await Assert.That(metrics.LastHandlerName).IsEqualTo("MyApp.Handlers.OrderHandler");
    await Assert.That(metrics.LastMessageTypeName).IsEqualTo("CreateOrder");
    await Assert.That(metrics.LastStatus).IsEqualTo(HandlerStatus.Success);
    await Assert.That(metrics.LastDurationMs).IsEqualTo(123.45);
    await Assert.That(metrics.LastStartTimestamp).IsEqualTo(1000L);
    await Assert.That(metrics.LastEndTimestamp).IsEqualTo(2000L);
  }

  [Test]
  public async Task RecordInvocation_WithFailedStatus_RecordsCorrectlyAsync() {
    // Arrange
    var metrics = new TestHandlerMetrics();

    // Act
    metrics.RecordInvocation(
        handlerName: "ErrorHandler",
        messageTypeName: "FailCommand",
        status: HandlerStatus.Failed,
        durationMs: 5.0,
        startTimestamp: 100L,
        endTimestamp: 200L);

    // Assert
    await Assert.That(metrics.LastStatus).IsEqualTo(HandlerStatus.Failed);
    await Assert.That(metrics.InvocationCount).IsEqualTo(1);
  }

  [Test]
  public async Task RecordInvocation_WithEarlyReturn_RecordsCorrectlyAsync() {
    // Arrange
    var metrics = new TestHandlerMetrics();

    // Act
    metrics.RecordInvocation(
        handlerName: "ValidationHandler",
        messageTypeName: "ValidateRequest",
        status: HandlerStatus.EarlyReturn,
        durationMs: 0.5,
        startTimestamp: 50L,
        endTimestamp: 55L);

    // Assert
    await Assert.That(metrics.LastStatus).IsEqualTo(HandlerStatus.EarlyReturn);
  }

  [Test]
  public async Task RecordInvocation_WithCancelled_RecordsCorrectlyAsync() {
    // Arrange
    var metrics = new TestHandlerMetrics();

    // Act
    metrics.RecordInvocation(
        handlerName: "LongRunningHandler",
        messageTypeName: "ProcessLargeData",
        status: HandlerStatus.Cancelled,
        durationMs: 5000.0,
        startTimestamp: 1000L,
        endTimestamp: 6000L);

    // Assert
    await Assert.That(metrics.LastStatus).IsEqualTo(HandlerStatus.Cancelled);
  }

  [Test]
  public async Task RecordInvocation_MultipleInvocations_TracksCountAsync() {
    // Arrange
    var metrics = new TestHandlerMetrics();

    // Act - record multiple invocations
    for (int i = 0; i < 5; i++) {
      metrics.RecordInvocation("Handler", "Message", HandlerStatus.Success, 10.0, i, i + 10);
    }

    // Assert
    await Assert.That(metrics.InvocationCount).IsEqualTo(5);
  }

  #endregion

  #region Test Implementation

  /// <summary>
  /// Test implementation of IHandlerMetrics for verification.
  /// </summary>
  private sealed class TestHandlerMetrics : IHandlerMetrics {
    public string? LastHandlerName { get; private set; }
    public string? LastMessageTypeName { get; private set; }
    public HandlerStatus LastStatus { get; private set; }
    public double LastDurationMs { get; private set; }
    public long LastStartTimestamp { get; private set; }
    public long LastEndTimestamp { get; private set; }
    public int InvocationCount { get; private set; }

    public void RecordInvocation(
        string handlerName,
        string messageTypeName,
        HandlerStatus status,
        double durationMs,
        long startTimestamp,
        long endTimestamp) {
      LastHandlerName = handlerName;
      LastMessageTypeName = messageTypeName;
      LastStatus = status;
      LastDurationMs = durationMs;
      LastStartTimestamp = startTimestamp;
      LastEndTimestamp = endTimestamp;
      InvocationCount++;
    }
  }

  #endregion
}
