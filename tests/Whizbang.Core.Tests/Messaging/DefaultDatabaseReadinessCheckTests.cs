using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for DefaultDatabaseReadinessCheck - default implementation that always returns true.
/// This is used when no specific database readiness implementation is provided.
/// </summary>
public class DefaultDatabaseReadinessCheckTests {
  [Test]
  public async Task IsReadyAsync_AlwaysReturnsTrue_WithDefaultCancellationTokenAsync() {
    // Arrange
    var readinessCheck = new DefaultDatabaseReadinessCheck();

    // Act
    var isReady = await readinessCheck.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsTrue()
      .Because("DefaultDatabaseReadinessCheck always returns true");
  }

  [Test]
  public async Task IsReadyAsync_AlwaysReturnsTrue_WithCustomCancellationTokenAsync() {
    // Arrange
    var readinessCheck = new DefaultDatabaseReadinessCheck();
    using var cts = new CancellationTokenSource();

    // Act
    var isReady = await readinessCheck.IsReadyAsync(cts.Token);

    // Assert
    await Assert.That(isReady).IsTrue()
      .Because("DefaultDatabaseReadinessCheck always returns true regardless of cancellation token");
  }

  [Test]
  public async Task IsReadyAsync_MultipleCalls_AlwaysReturnsTrueAsync() {
    // Arrange
    var readinessCheck = new DefaultDatabaseReadinessCheck();

    // Act
    var result1 = await readinessCheck.IsReadyAsync();
    var result2 = await readinessCheck.IsReadyAsync();
    var result3 = await readinessCheck.IsReadyAsync();

    // Assert
    await Assert.That(result1).IsTrue()
      .Because("First call should return true");
    await Assert.That(result2).IsTrue()
      .Because("Second call should return true");
    await Assert.That(result3).IsTrue()
      .Because("Third call should return true");
  }

  [Test]
  public async Task IsReadyAsync_WithCancelledToken_StillReturnsTrueAsync() {
    // Arrange
    var readinessCheck = new DefaultDatabaseReadinessCheck();
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act
    var isReady = await readinessCheck.IsReadyAsync(cts.Token);

    // Assert
    await Assert.That(isReady).IsTrue()
      .Because("DefaultDatabaseReadinessCheck ignores cancellation token and always returns true");
  }

  [Test]
  public async Task IsReadyAsync_ImplementsInterface_IDatabaseReadinessCheckAsync() {
    // Arrange
    IDatabaseReadinessCheck readinessCheck = new DefaultDatabaseReadinessCheck();

    // Act
    var isReady = await readinessCheck.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsTrue()
      .Because("DefaultDatabaseReadinessCheck implements IDatabaseReadinessCheck and always returns true");
  }
}
