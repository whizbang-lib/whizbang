using TUnit.Core;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>
/// Tests for DispatchOptions configuration.
/// Validates default values, fluent API, and validation behavior.
/// </summary>
public class DispatchOptionsTests {
  // ========================================
  // Default Value Tests
  // ========================================

  [Test]
  public async Task Default_CancellationToken_IsNone_Async() {
    // Arrange
    var options = new DispatchOptions();

    // Assert - Default should be CancellationToken.None
    await Assert.That(options.CancellationToken).IsEqualTo(CancellationToken.None);
  }

  [Test]
  public async Task Default_Timeout_IsNull_Async() {
    // Arrange
    var options = new DispatchOptions();

    // Assert - Default should be no timeout
    await Assert.That(options.Timeout).IsNull();
  }

  [Test]
  public async Task Default_StaticProperty_ReturnsNewInstanceEachTimeAsync() {
    // Act
    var options1 = DispatchOptions.Default;
    var options2 = DispatchOptions.Default;

    // Assert - Each call should return a new instance
    await Assert.That(options1).IsNotSameReferenceAs(options2);
  }

  // ========================================
  // Fluent API Tests
  // ========================================

  [Test]
  public async Task WithCancellationToken_SetsToken_ReturnsSelfAsync() {
    // Arrange
    using var cts = new CancellationTokenSource();
    var options = new DispatchOptions();

    // Act
    var result = options.WithCancellationToken(cts.Token);

    // Assert
    await Assert.That(options.CancellationToken).IsEqualTo(cts.Token);
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task WithTimeout_SetsTimeout_ReturnsSelfAsync() {
    // Arrange
    var timeout = TimeSpan.FromSeconds(30);
    var options = new DispatchOptions();

    // Act
    var result = options.WithTimeout(timeout);

    // Assert
    await Assert.That(options.Timeout).IsEqualTo(timeout);
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task WithTimeout_ZeroValue_SetsTimeoutAsync() {
    // Arrange
    var options = new DispatchOptions();

    // Act
    var result = options.WithTimeout(TimeSpan.Zero);

    // Assert - Zero is valid (immediate timeout)
    await Assert.That(options.Timeout).IsEqualTo(TimeSpan.Zero);
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task WithTimeout_NegativeValue_ThrowsArgumentOutOfRangeExceptionAsync() {
    // Arrange
    var options = new DispatchOptions();
    var negativeTimeout = TimeSpan.FromSeconds(-1);

    // Act & Assert
    await Assert.That(() => options.WithTimeout(negativeTimeout))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task FluentApi_CanChainMultipleCalls_Async() {
    // Arrange
    using var cts = new CancellationTokenSource();
    var timeout = TimeSpan.FromMinutes(5);

    // Act
    var options = new DispatchOptions()
      .WithCancellationToken(cts.Token)
      .WithTimeout(timeout);

    // Assert
    await Assert.That(options.CancellationToken).IsEqualTo(cts.Token);
    await Assert.That(options.Timeout).IsEqualTo(timeout);
  }

  // ========================================
  // Property Setter Tests (for coverage)
  // ========================================

  [Test]
  public async Task CancellationToken_PropertySetter_WorksAsync() {
    // Arrange
    using var cts = new CancellationTokenSource();
    var options = new DispatchOptions {
      // Act
      CancellationToken = cts.Token
    };

    // Assert
    await Assert.That(options.CancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task Timeout_PropertySetter_WorksAsync() {
    // Arrange
    var timeout = TimeSpan.FromSeconds(10);
    var options = new DispatchOptions {
      // Act
      Timeout = timeout
    };

    // Assert
    await Assert.That(options.Timeout).IsEqualTo(timeout);
  }

  [Test]
  public async Task Timeout_PropertySetter_AcceptsNullAsync() {
    // Arrange
    var options = new DispatchOptions { Timeout = TimeSpan.FromSeconds(10) };

    // Act
    options.Timeout = null;

    // Assert
    await Assert.That(options.Timeout).IsNull();
  }

  // ========================================
  // WaitForPerspectives Tests
  // ========================================

  [Test]
  public async Task Default_WaitForPerspectives_IsFalseAsync() {
    // Arrange
    var options = new DispatchOptions();

    // Assert - Default should be false (don't wait for perspectives)
    await Assert.That(options.WaitForPerspectives).IsFalse();
  }

  [Test]
  public async Task Default_PerspectiveWaitTimeout_Is30SecondsAsync() {
    // Arrange
    var options = new DispatchOptions();

    // Assert - Default timeout should be 30 seconds
    await Assert.That(options.PerspectiveWaitTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task WithPerspectiveWait_SetsWaitForPerspectivesToTrueAsync() {
    // Arrange
    var options = new DispatchOptions();

    // Act
    var result = options.WithPerspectiveWait();

    // Assert
    await Assert.That(options.WaitForPerspectives).IsTrue();
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task WithPerspectiveWait_WithTimeout_SetsTimeoutAsync() {
    // Arrange
    var options = new DispatchOptions();
    var customTimeout = TimeSpan.FromMinutes(2);

    // Act
    var result = options.WithPerspectiveWait(customTimeout);

    // Assert
    await Assert.That(options.WaitForPerspectives).IsTrue();
    await Assert.That(options.PerspectiveWaitTimeout).IsEqualTo(customTimeout);
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task WithPerspectiveWait_NoTimeout_KeepsDefaultTimeoutAsync() {
    // Arrange
    var options = new DispatchOptions();
    var defaultTimeout = options.PerspectiveWaitTimeout;

    // Act
    options.WithPerspectiveWait();

    // Assert - timeout should remain at default
    await Assert.That(options.PerspectiveWaitTimeout).IsEqualTo(defaultTimeout);
  }

  [Test]
  public async Task WaitForPerspectives_PropertySetter_WorksAsync() {
    // Arrange
    var options = new DispatchOptions {
      // Act
      WaitForPerspectives = true
    };

    // Assert
    await Assert.That(options.WaitForPerspectives).IsTrue();
  }

  [Test]
  public async Task PerspectiveWaitTimeout_PropertySetter_WorksAsync() {
    // Arrange
    var options = new DispatchOptions();
    var timeout = TimeSpan.FromMinutes(5);

    // Act
    options.PerspectiveWaitTimeout = timeout;

    // Assert
    await Assert.That(options.PerspectiveWaitTimeout).IsEqualTo(timeout);
  }

  [Test]
  public async Task FluentApi_CanChainWithPerspectiveWaitAsync() {
    // Arrange
    using var cts = new CancellationTokenSource();
    var timeout = TimeSpan.FromMinutes(5);
    var perspectiveTimeout = TimeSpan.FromMinutes(2);

    // Act
    var options = new DispatchOptions()
      .WithCancellationToken(cts.Token)
      .WithTimeout(timeout)
      .WithPerspectiveWait(perspectiveTimeout);

    // Assert
    await Assert.That(options.CancellationToken).IsEqualTo(cts.Token);
    await Assert.That(options.Timeout).IsEqualTo(timeout);
    await Assert.That(options.WaitForPerspectives).IsTrue();
    await Assert.That(options.PerspectiveWaitTimeout).IsEqualTo(perspectiveTimeout);
  }
}
