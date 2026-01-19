using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for DefaultTransportReadinessCheck - verifies always-ready behavior.
/// </summary>
public class DefaultTransportReadinessCheckTests {
  [Test]
  public async Task IsReadyAsync_Always_ReturnsTrueAsync() {
    // Arrange
    var check = new DefaultTransportReadinessCheck();

    // Act
    var result = await check.IsReadyAsync();

    // Assert
    await Assert.That(result).IsTrue()
      .Because("DefaultTransportReadinessCheck should always return true");
  }

  [Test]
  public async Task IsReadyAsync_MultipleCalls_AlwaysReturnsTrueAsync() {
    // Arrange
    var check = new DefaultTransportReadinessCheck();

    // Act
    var result1 = await check.IsReadyAsync();
    var result2 = await check.IsReadyAsync();
    var result3 = await check.IsReadyAsync();

    // Assert
    await Assert.That(result1).IsTrue();
    await Assert.That(result2).IsTrue();
    await Assert.That(result3).IsTrue();
  }

  [Test]
  public async Task IsReadyAsync_Cancellation_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var check = new DefaultTransportReadinessCheck();
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () => await check.IsReadyAsync(cts.Token))
      .Throws<OperationCanceledException>()
      .Because("Cancelled token should throw OperationCanceledException");
  }
}
