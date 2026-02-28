using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for HandlerMetrics which records handler invocation metrics.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/HandlerMetrics.cs</code-under-test>
public class HandlerMetricsTests {
  #region Constructor Tests

  [Test]
  public async Task Constructor_WithValidOptions_DoesNotThrowAsync() {
    // Arrange
    var options = _createOptionsMonitor(new MetricsOptions { Enabled = true });

    // Act
    var metrics = new HandlerMetrics(options);

    // Assert
    await Assert.That(metrics).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new HandlerMetrics(null!)).Throws<ArgumentNullException>();
  }

  #endregion

  #region RecordInvocation - Disabled Metrics

  [Test]
  public void RecordInvocation_WhenMetricsDisabled_DoesNotThrow() {
    // Arrange
    var options = _createOptionsMonitor(new MetricsOptions { Enabled = false });
    var metrics = new HandlerMetrics(options);

    // Act - Should not throw even with disabled metrics
    // If this throws, the test will fail
    metrics.RecordInvocation(
        handlerName: "TestHandler",
        messageTypeName: "TestMessage",
        status: HandlerStatus.Success,
        durationMs: 100.0,
        startTimestamp: 0,
        endTimestamp: 1000);
  }

  [Test]
  public void RecordInvocation_WhenComponentNotEnabled_DoesNotThrow() {
    // Arrange - Enabled but Handlers not in Components
    var options = _createOptionsMonitor(new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.EventStore  // No Handlers
    });
    var metrics = new HandlerMetrics(options);

    // Act - Should early return without throwing
    metrics.RecordInvocation(
        handlerName: "TestHandler",
        messageTypeName: "TestMessage",
        status: HandlerStatus.Success,
        durationMs: 100.0,
        startTimestamp: 0,
        endTimestamp: 1000);
  }

  #endregion

  #region RecordInvocation - Enabled Metrics

  [Test]
  public void RecordInvocation_WhenEnabled_DoesNotThrow() {
    // Arrange
    var options = _createOptionsMonitor(new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.Handlers
    });
    var metrics = new HandlerMetrics(options);

    // Act - Should record metrics without throwing
    metrics.RecordInvocation(
        handlerName: "OrderReceptor",
        messageTypeName: "CreateOrderCommand",
        status: HandlerStatus.Success,
        durationMs: 50.5,
        startTimestamp: 1000,
        endTimestamp: 2000);
  }

  [Test]
  public void RecordInvocation_WithAllStatuses_DoesNotThrow() {
    // Arrange
    var options = _createOptionsMonitor(new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.Handlers
    });
    var metrics = new HandlerMetrics(options);

    // Act - Test all handler statuses
    metrics.RecordInvocation("Handler1", "Message1", HandlerStatus.Success, 10.0, 0, 100);
    metrics.RecordInvocation("Handler2", "Message2", HandlerStatus.Failed, 20.0, 0, 200);
    metrics.RecordInvocation("Handler3", "Message3", HandlerStatus.EarlyReturn, 5.0, 0, 50);
    metrics.RecordInvocation("Handler4", "Message4", HandlerStatus.Cancelled, 30.0, 0, 300);
  }

  #endregion

  #region RecordInvocation - Tag Configuration

  [Test]
  public void RecordInvocation_WithHandlerNameTagDisabled_DoesNotThrow() {
    // Arrange
    var options = _createOptionsMonitor(new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.Handlers,
      IncludeHandlerNameTag = false
    });
    var metrics = new HandlerMetrics(options);

    // Act - Should record without handler tag
    metrics.RecordInvocation(
        handlerName: "TestHandler",
        messageTypeName: "TestMessage",
        status: HandlerStatus.Success,
        durationMs: 100.0,
        startTimestamp: 0,
        endTimestamp: 1000);
  }

  [Test]
  public void RecordInvocation_WithMessageTypeTagDisabled_DoesNotThrow() {
    // Arrange
    var options = _createOptionsMonitor(new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.Handlers,
      IncludeMessageTypeTag = false
    });
    var metrics = new HandlerMetrics(options);

    // Act - Should record without message_type tag
    metrics.RecordInvocation(
        handlerName: "TestHandler",
        messageTypeName: "TestMessage",
        status: HandlerStatus.Success,
        durationMs: 100.0,
        startTimestamp: 0,
        endTimestamp: 1000);
  }

  [Test]
  public void RecordInvocation_WithAllTagsDisabled_DoesNotThrow() {
    // Arrange
    var options = _createOptionsMonitor(new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.Handlers,
      IncludeHandlerNameTag = false,
      IncludeMessageTypeTag = false
    });
    var metrics = new HandlerMetrics(options);

    // Act - Should record with minimal tags
    metrics.RecordInvocation(
        handlerName: "TestHandler",
        messageTypeName: "TestMessage",
        status: HandlerStatus.Success,
        durationMs: 100.0,
        startTimestamp: 0,
        endTimestamp: 1000);
  }

  #endregion

  #region RecordInvocation - Edge Cases

  [Test]
  public void RecordInvocation_WithEmptyHandlerName_DoesNotThrow() {
    // Arrange
    var options = _createOptionsMonitor(new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.Handlers
    });
    var metrics = new HandlerMetrics(options);

    // Act - Empty handler name should be handled gracefully
    metrics.RecordInvocation(
        handlerName: "",
        messageTypeName: "TestMessage",
        status: HandlerStatus.Success,
        durationMs: 100.0,
        startTimestamp: 0,
        endTimestamp: 1000);
  }

  [Test]
  public void RecordInvocation_WithNullHandlerName_DoesNotThrow() {
    // Arrange
    var options = _createOptionsMonitor(new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.Handlers
    });
    var metrics = new HandlerMetrics(options);

    // Act - Null handler name should be handled gracefully
    metrics.RecordInvocation(
        handlerName: null!,
        messageTypeName: "TestMessage",
        status: HandlerStatus.Success,
        durationMs: 100.0,
        startTimestamp: 0,
        endTimestamp: 1000);
  }

  [Test]
  public void RecordInvocation_WithZeroDuration_DoesNotThrow() {
    // Arrange
    var options = _createOptionsMonitor(new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.Handlers
    });
    var metrics = new HandlerMetrics(options);

    // Act - Zero duration is valid
    metrics.RecordInvocation(
        handlerName: "FastHandler",
        messageTypeName: "TestMessage",
        status: HandlerStatus.Success,
        durationMs: 0.0,
        startTimestamp: 0,
        endTimestamp: 0);
  }

  [Test]
  public void RecordInvocation_WithNegativeDuration_DoesNotThrow() {
    // Arrange - Negative duration shouldn't happen but shouldn't crash
    var options = _createOptionsMonitor(new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.Handlers
    });
    var metrics = new HandlerMetrics(options);

    // Act - Negative duration handled gracefully
    metrics.RecordInvocation(
        handlerName: "TestHandler",
        messageTypeName: "TestMessage",
        status: HandlerStatus.Success,
        durationMs: -1.0,
        startTimestamp: 1000,
        endTimestamp: 0);
  }

  #endregion

  #region NullHandlerMetrics Tests

  [Test]
  public async Task NullHandlerMetrics_Instance_IsSingletonAsync() {
    // Arrange & Act
    var instance1 = NullHandlerMetrics.Instance;
    var instance2 = NullHandlerMetrics.Instance;

    // Assert
    await Assert.That(instance1).IsSameReferenceAs(instance2);
  }

  [Test]
  public void NullHandlerMetrics_RecordInvocation_DoesNothing() {
    // Arrange
    var metrics = NullHandlerMetrics.Instance;

    // Act - No-op implementation should not throw
    metrics.RecordInvocation(
        handlerName: "TestHandler",
        messageTypeName: "TestMessage",
        status: HandlerStatus.Success,
        durationMs: 100.0,
        startTimestamp: 0,
        endTimestamp: 1000);
  }

  [Test]
  public async Task NullHandlerMetrics_ImplementsInterface_CorrectlyAsync() {
    // Arrange
    IHandlerMetrics metrics = NullHandlerMetrics.Instance;

    // Assert
    await Assert.That(metrics).IsNotNull();
    await Assert.That(metrics is NullHandlerMetrics).IsTrue();
  }

  #endregion

  #region Helper Methods

  private static TestOptionsMonitor<MetricsOptions> _createOptionsMonitor(MetricsOptions options) {
    return new TestOptionsMonitor<MetricsOptions>(options);
  }

  /// <summary>
  /// Simple test implementation of IOptionsMonitor for testing.
  /// </summary>
  private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> {
    private readonly T _currentValue;

    public T CurrentValue => _currentValue;

    public TestOptionsMonitor(T value) {
      _currentValue = value;
    }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
  }

  #endregion
}
