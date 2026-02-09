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
    var options = new DispatchOptions();

    // Act
    options.CancellationToken = cts.Token;

    // Assert
    await Assert.That(options.CancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task Timeout_PropertySetter_WorksAsync() {
    // Arrange
    var timeout = TimeSpan.FromSeconds(10);
    var options = new DispatchOptions();

    // Act
    options.Timeout = timeout;

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
}
