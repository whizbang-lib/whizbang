using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for <see cref="HandlerStatus"/>.
/// Validates enum values and their meanings.
/// </summary>
[Category("Core")]
[Category("Tracing")]
public class HandlerStatusTests {

  [Test]
  public async Task Success_HasCorrectValue_ZeroAsync() {
    // Arrange & Act
    var status = HandlerStatus.Success;

    // Assert - Success is the default (0)
    await Assert.That((int)status).IsEqualTo(0);
  }

  [Test]
  public async Task Failed_HasCorrectValue_OneAsync() {
    // Arrange & Act
    var status = HandlerStatus.Failed;

    // Assert
    await Assert.That((int)status).IsEqualTo(1);
  }

  [Test]
  public async Task EarlyReturn_HasCorrectValue_TwoAsync() {
    // Arrange & Act
    var status = HandlerStatus.EarlyReturn;

    // Assert
    await Assert.That((int)status).IsEqualTo(2);
  }

  [Test]
  public async Task Cancelled_HasCorrectValue_ThreeAsync() {
    // Arrange & Act
    var status = HandlerStatus.Cancelled;

    // Assert
    await Assert.That((int)status).IsEqualTo(3);
  }

  [Test]
  public async Task AllValues_AreDefined_InEnumAsync() {
    // Arrange & Act
    var values = Enum.GetValues<HandlerStatus>();

    // Assert - exactly 4 values
    await Assert.That(values.Length).IsEqualTo(4);
    await Assert.That(values).Contains(HandlerStatus.Success);
    await Assert.That(values).Contains(HandlerStatus.Failed);
    await Assert.That(values).Contains(HandlerStatus.EarlyReturn);
    await Assert.That(values).Contains(HandlerStatus.Cancelled);
  }

  [Test]
  public async Task DefaultValue_IsSuccess_Async() {
    // Arrange & Act - default for enum is 0
    HandlerStatus status = default;

    // Assert
    await Assert.That(status).IsEqualTo(HandlerStatus.Success);
  }
}
