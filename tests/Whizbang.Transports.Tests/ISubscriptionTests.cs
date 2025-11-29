using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for ISubscription interface.
/// Represents an active subscription to a transport that can be controlled.
/// Following TDD: These tests are written BEFORE the interface implementation.
/// </summary>
public class ISubscriptionTests {
  [Test]
  public async Task ISubscription_Dispose_UnsubscribesAsync() {
    // Arrange
    var subscription = CreateTestSubscription();
    var isActive = subscription.IsActive;

    // Act
    subscription.Dispose();

    // Assert
    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task ISubscription_Pause_SetsIsActiveFalseAsync() {
    // Arrange
    var subscription = CreateTestSubscription();

    // Act
    await subscription.PauseAsync();

    // Assert
    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task ISubscription_Resume_SetsIsActiveTrueAsync() {
    // Arrange
    var subscription = CreateTestSubscription();
    await subscription.PauseAsync();

    // Act
    await subscription.ResumeAsync();

    // Assert
    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task ISubscription_InitialState_IsActiveAsync() {
    // Arrange & Act
    var subscription = CreateTestSubscription();

    // Assert
    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task ISubscription_DisposeMultipleTimes_DoesNotThrowAsync() {
    // Arrange
    var subscription = CreateTestSubscription();

    // Act & Assert - Should not throw
    subscription.Dispose();
    subscription.Dispose();
    subscription.Dispose();
  }

  [Test]
  public async Task ISubscription_PauseWhenPaused_DoesNotThrowAsync() {
    // Arrange
    var subscription = CreateTestSubscription();
    await subscription.PauseAsync();

    // Act & Assert - Should not throw
    await subscription.PauseAsync();
  }

  [Test]
  public async Task ISubscription_ResumeWhenActive_DoesNotThrowAsync() {
    // Arrange
    var subscription = CreateTestSubscription();

    // Act & Assert - Should not throw
    await subscription.ResumeAsync();
  }

  // Helper methods
  private static ISubscription CreateTestSubscription() {
    // This will use a test implementation once ISubscription is defined
    // For now, this will fail compilation - that's expected in RED phase
    return new TestSubscription();
  }

  // Test implementation
  private class TestSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;

    public Task PauseAsync() {
      IsActive = false;
      return Task.CompletedTask;
    }

    public Task ResumeAsync() {
      IsActive = true;
      return Task.CompletedTask;
    }

    public void Dispose() {
      IsActive = false;
    }
  }
}
