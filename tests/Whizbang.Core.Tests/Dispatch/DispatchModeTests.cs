using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>
/// Tests for DispatchMode enum which defines routing destinations for cascaded messages.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/DispatchMode.cs</code-under-test>
public class DispatchModeTests {
  #region Flag Values

  [Test]
  public async Task None_HasValue_ZeroAsync() {
    // Arrange
    var mode = DispatchMode.None;

    // Assert
    await Assert.That((int)mode).IsEqualTo(0);
  }

  [Test]
  public async Task Local_HasValue_OneAsync() {
    // Arrange
    var mode = DispatchMode.Local;

    // Assert
    await Assert.That((int)mode).IsEqualTo(1);
  }

  [Test]
  public async Task Outbox_HasValue_TwoAsync() {
    // Arrange
    var mode = DispatchMode.Outbox;

    // Assert
    await Assert.That((int)mode).IsEqualTo(2);
  }

  [Test]
  public async Task Both_HasValue_ThreeAsync() {
    // Arrange
    var mode = DispatchMode.Both;

    // Assert - Both should be Local | Outbox = 1 | 2 = 3
    await Assert.That((int)mode).IsEqualTo(3);
  }

  #endregion

  #region Flag Combinations

  [Test]
  public async Task Both_IsCombination_OfLocalAndOutboxAsync() {
    // Arrange
    var mode = DispatchMode.Both;

    // Assert
    await Assert.That(mode).IsEqualTo(DispatchMode.Local | DispatchMode.Outbox);
  }

  [Test]
  public async Task LocalOrOutbox_Equals_BothAsync() {
    // Arrange
    var combined = DispatchMode.Local | DispatchMode.Outbox;

    // Assert
    await Assert.That(combined).IsEqualTo(DispatchMode.Both);
  }

  #endregion

  #region HasFlag Tests

  [Test]
  public async Task Both_HasFlag_LocalAsync() {
    // Arrange
    var mode = DispatchMode.Both;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.Local)).IsTrue();
  }

  [Test]
  public async Task Both_HasFlag_OutboxAsync() {
    // Arrange
    var mode = DispatchMode.Both;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.Outbox)).IsTrue();
  }

  [Test]
  public async Task Local_DoesNotHaveFlag_OutboxAsync() {
    // Arrange
    var mode = DispatchMode.Local;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.Outbox)).IsFalse();
  }

  [Test]
  public async Task Outbox_DoesNotHaveFlag_LocalAsync() {
    // Arrange
    var mode = DispatchMode.Outbox;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.Local)).IsFalse();
  }

  [Test]
  public async Task None_DoesNotHaveFlag_LocalAsync() {
    // Arrange
    var mode = DispatchMode.None;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.Local)).IsFalse();
  }

  [Test]
  public async Task None_DoesNotHaveFlag_OutboxAsync() {
    // Arrange
    var mode = DispatchMode.None;

    // Assert
    await Assert.That(mode.HasFlag(DispatchMode.Outbox)).IsFalse();
  }

  #endregion
}
