using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for TraceResult which contains the outcome of traced operations.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/TraceResult.cs</code-under-test>
public class TraceResultTests {
  #region Required Property Tests

  [Test]
  public async Task Success_RequiredProperty_CanBeSetTrueAsync() {
    // Arrange & Act
    var result = _createResult(success: true);

    // Assert
    await Assert.That(result.Success).IsTrue();
  }

  [Test]
  public async Task Success_RequiredProperty_CanBeSetFalseAsync() {
    // Arrange & Act
    var result = _createResult(success: false);

    // Assert
    await Assert.That(result.Success).IsFalse();
  }

  [Test]
  public async Task Duration_RequiredProperty_CanBeSetAsync() {
    // Arrange
    var duration = TimeSpan.FromMilliseconds(42.5);

    // Act
    var result = _createResult(duration: duration);

    // Assert
    await Assert.That(result.Duration).IsEqualTo(duration);
  }

  [Test]
  public async Task Status_RequiredProperty_CanBeSetAsync() {
    // Arrange
    const string status = "Completed";

    // Act
    var result = _createResult(status: status);

    // Assert
    await Assert.That(result.Status).IsEqualTo(status);
  }

  #endregion

  #region Optional Property Tests

  [Test]
  public async Task Exception_OptionalProperty_CanBeNullAsync() {
    // Arrange & Act
    var result = _createResult();

    // Assert
    await Assert.That(result.Exception).IsNull();
  }

  [Test]
  public async Task Exception_OptionalProperty_CanBeSetAsync() {
    // Arrange
    var exception = new InvalidOperationException("Test error");

    // Act
    var result = _createResult() with { Exception = exception };

    // Assert
    await Assert.That(result.Exception).IsNotNull();
    await Assert.That(result.Exception!.Message).IsEqualTo("Test error");
  }

  #endregion

  #region Default Value Tests

  [Test]
  public async Task Properties_DefaultValue_IsEmptyDictionaryAsync() {
    // Arrange & Act
    var result = _createResult();

    // Assert
    await Assert.That(result.Properties).IsNotNull();
    await Assert.That(result.Properties.Count).IsEqualTo(0);
  }

  #endregion

  #region Properties Dictionary Tests

  [Test]
  public async Task Properties_CanBePopulatedAsync() {
    // Arrange
    var result = _createResult();

    // Act
    result.Properties["resultCode"] = 200;
    result.Properties["handlerOutput"] = "processed";

    // Assert
    await Assert.That(result.Properties.Count).IsEqualTo(2);
    await Assert.That(result.Properties["resultCode"]).IsEqualTo(200);
  }

  #endregion

  #region Factory Method Tests

  [Test]
  public async Task Completed_CreatesSuccessResultAsync() {
    // Arrange
    var duration = TimeSpan.FromMilliseconds(100);

    // Act
    var result = TraceResult.Completed(duration);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.Status).IsEqualTo("Completed");
    await Assert.That(result.Duration).IsEqualTo(duration);
    await Assert.That(result.Exception).IsNull();
  }

  [Test]
  public async Task Failed_CreatesFailureResultAsync() {
    // Arrange
    var duration = TimeSpan.FromMilliseconds(50);
    var exception = new InvalidOperationException("Handler failed");

    // Act
    var result = TraceResult.Failed(duration, exception);

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Status).IsEqualTo("Failed");
    await Assert.That(result.Duration).IsEqualTo(duration);
    await Assert.That(result.Exception).IsEqualTo(exception);
  }

  [Test]
  public async Task EarlyReturn_CreatesEarlyReturnResultAsync() {
    // Arrange
    var duration = TimeSpan.FromMilliseconds(1);

    // Act
    var result = TraceResult.EarlyReturn(duration);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.Status).IsEqualTo("EarlyReturn");
    await Assert.That(result.Duration).IsEqualTo(duration);
    await Assert.That(result.Exception).IsNull();
  }

  #endregion

  #region Record Behavior Tests

  [Test]
  public async Task TraceResult_IsRecordAsync() {
    // Arrange
    var type = typeof(TraceResult);

    // Assert - Records support with expressions
    await Assert.That(type.IsClass).IsTrue();
  }

  [Test]
  public async Task TraceResult_SupportsWith_ExpressionAsync() {
    // Arrange
    var original = _createResult(status: "Original");

    // Act
    var modified = original with { Status = "Modified" };

    // Assert
    await Assert.That(original.Status).IsEqualTo("Original");
    await Assert.That(modified.Status).IsEqualTo("Modified");
  }

  #endregion

  #region Helper Methods

  private static TraceResult _createResult(
      bool? success = null,
      TimeSpan? duration = null,
      string? status = null) {
    return new TraceResult {
      Success = success ?? true,
      Duration = duration ?? TimeSpan.FromMilliseconds(10),
      Status = status ?? "Completed"
    };
  }

  #endregion
}
