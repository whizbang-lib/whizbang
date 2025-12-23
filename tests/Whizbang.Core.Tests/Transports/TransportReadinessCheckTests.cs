using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tests for ITransportReadinessCheck interface and implementations.
/// Validates that transport readiness can be checked before attempting to publish messages.
/// </summary>
public class TransportReadinessCheckTests {
  [Test]
  public async Task AlwaysReadyCheck_ReturnsTrue_WhenCalledAsync() {
    // Arrange
    var check = new AlwaysReadyCheck();

    // Act
    var isReady = await check.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsTrue()
      .Because("AlwaysReadyCheck should always return true");
  }

  [Test]
  public async Task NeverReadyCheck_ReturnsFalse_WhenCalledAsync() {
    // Arrange
    var check = new NeverReadyCheck();

    // Act
    var isReady = await check.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsFalse()
      .Because("NeverReadyCheck should always return false");
  }

  [Test]
  public async Task ConfigurableCheck_ReturnsConfiguredValue_WhenCalledAsync() {
    // Arrange
    var readyCheck = new ConfigurableReadinessCheck(isReady: true);
    var notReadyCheck = new ConfigurableReadinessCheck(isReady: false);

    // Act
    var isReady = await readyCheck.IsReadyAsync();
    var isNotReady = await notReadyCheck.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsTrue()
      .Because("ConfigurableReadinessCheck should return the configured value");
    await Assert.That(isNotReady).IsFalse()
      .Because("ConfigurableReadinessCheck should return the configured value");
  }

  [Test]
  public async Task ConfigurableCheck_RespectsCancellationToken_WhenCancelledAsync() {
    // Arrange
    var check = new ConfigurableReadinessCheck(isReady: true);
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await check.IsReadyAsync(cts.Token)
    );
  }

  [Test]
  public async Task ConfigurableCheck_CanChangeState_DynamicallyAsync() {
    // Arrange
    var check = new ConfigurableReadinessCheck(isReady: false);

    // Act - Initially not ready
    var initialState = await check.IsReadyAsync();

    // Change to ready
    check.SetReady(true);
    var readyState = await check.IsReadyAsync();

    // Change back to not ready
    check.SetReady(false);
    var notReadyState = await check.IsReadyAsync();

    // Assert
    await Assert.That(initialState).IsFalse();
    await Assert.That(readyState).IsTrue();
    await Assert.That(notReadyState).IsFalse();
  }
}

/// <summary>
/// Test implementation that always returns true.
/// Useful for testing scenarios where transport is always ready.
/// </summary>
internal sealed class AlwaysReadyCheck : ITransportReadinessCheck {
  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    return Task.FromResult(true);
  }
}

/// <summary>
/// Test implementation that always returns false.
/// Useful for testing scenarios where transport is never ready.
/// </summary>
internal sealed class NeverReadyCheck : ITransportReadinessCheck {
  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    return Task.FromResult(false);
  }
}

/// <summary>
/// Test implementation with configurable readiness state.
/// Useful for testing dynamic readiness scenarios.
/// </summary>
internal sealed class ConfigurableReadinessCheck(bool isReady) : ITransportReadinessCheck {
  private bool _isReady = isReady;

  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult(_isReady);
  }

  public void SetReady(bool isReady) {
    _isReady = isReady;
  }
}
