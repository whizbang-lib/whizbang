using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Hosting.RabbitMQ.Tests;

/// <summary>
/// Tests for RabbitMQ readiness check implementation.
/// </summary>
public class RabbitMQReadinessCheckTests {
  [Test]
  public async Task IsReadyAsync_WithOpenConnection_ReturnsTrueAsync() {
    // Arrange
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()), isOpen: true);
    var readinessCheck = new RabbitMQReadinessCheck(fakeConnection);

    // Act
    var result = await readinessCheck.IsReadyAsync();

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsReadyAsync_WithClosedConnection_ReturnsFalseAsync() {
    // Arrange
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()), isOpen: false);
    var readinessCheck = new RabbitMQReadinessCheck(fakeConnection);

    // Act
    var result = await readinessCheck.IsReadyAsync();

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsReadyAsync_RespectsCancellationToken_WhenCancelledAsync() {
    // Arrange
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()), isOpen: true);
    var readinessCheck = new RabbitMQReadinessCheck(fakeConnection);
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert - should not throw, just return current state
    var result = await readinessCheck.IsReadyAsync(cts.Token);
    await Assert.That(result).IsTrue(); // Connection is open
  }
}
