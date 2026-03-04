using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tests for <see cref="ITransportWithRecovery"/> interface behavior.
/// These tests verify the contract that transports with recovery support must implement.
/// </summary>
/// <tests>src/Whizbang.Core/Transports/ITransportWithRecovery.cs</tests>
public class ITransportWithRecoveryTests {
  #region SetRecoveryHandler Tests

  [Test]
  public async Task SetRecoveryHandler_WithValidHandler_DoesNotThrowAsync() {
    // Arrange
    var transport = new TestTransportWithRecovery();
    Func<CancellationToken, Task> handler = _ => Task.CompletedTask;

    // Act & Assert - should not throw
    transport.SetRecoveryHandler(handler);
    await Assert.That(transport.RecoveryHandler).IsNotNull();
  }

  [Test]
  public async Task SetRecoveryHandler_WithNullHandler_AcceptsNullAsync() {
    // Arrange
    var transport = new TestTransportWithRecovery();
    transport.SetRecoveryHandler(_ => Task.CompletedTask);

    // Act - setting null clears the handler
    transport.SetRecoveryHandler(null!);

    // Assert
    await Assert.That(transport.RecoveryHandler).IsNull();
  }

  [Test]
  public async Task SetRecoveryHandler_CalledMultipleTimes_ReplacesHandlerAsync() {
    // Arrange
    var transport = new TestTransportWithRecovery();
    var callCount = 0;
    Func<CancellationToken, Task> handler1 = _ => { callCount = 1; return Task.CompletedTask; };
    Func<CancellationToken, Task> handler2 = _ => { callCount = 2; return Task.CompletedTask; };

    // Act
    transport.SetRecoveryHandler(handler1);
    transport.SetRecoveryHandler(handler2);
    await transport.SimulateRecoveryAsync(CancellationToken.None);

    // Assert - handler2 should be called, not handler1
    await Assert.That(callCount).IsEqualTo(2);
  }

  #endregion

  #region Recovery Invocation Tests

  [Test]
  public async Task RecoveryHandler_WhenInvoked_ReceivesCancellationTokenAsync() {
    // Arrange
    var transport = new TestTransportWithRecovery();
    CancellationToken receivedToken = default;
    using var cts = new CancellationTokenSource();

    transport.SetRecoveryHandler(ct => {
      receivedToken = ct;
      return Task.CompletedTask;
    });

    // Act
    await transport.SimulateRecoveryAsync(cts.Token);

    // Assert
    await Assert.That(receivedToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task RecoveryHandler_WhenNotSet_SimulateRecoveryDoesNotThrowAsync() {
    // Arrange
    var transport = new TestTransportWithRecovery();
    // Don't set a handler

    // Act - should not throw when no handler is set
    await transport.SimulateRecoveryAsync(CancellationToken.None);

    // Assert - handler should still be null
    await Assert.That(transport.RecoveryHandler).IsNull();
  }

  #endregion

  #region Test Implementation

  /// <summary>
  /// Test implementation of ITransportWithRecovery for testing the interface contract.
  /// </summary>
  private sealed class TestTransportWithRecovery : ITransportWithRecovery {
    public Func<CancellationToken, Task>? RecoveryHandler { get; private set; }

    public void SetRecoveryHandler(Func<CancellationToken, Task>? onRecovered) {
      RecoveryHandler = onRecovered;
    }

    /// <summary>
    /// Simulates a connection recovery event for testing.
    /// </summary>
    public async Task SimulateRecoveryAsync(CancellationToken cancellationToken) {
      if (RecoveryHandler != null) {
        await RecoveryHandler(cancellationToken);
      }
    }
  }

  #endregion
}
