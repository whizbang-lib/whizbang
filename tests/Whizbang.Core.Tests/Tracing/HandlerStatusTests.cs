using TUnit.Core;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for <see cref="HandlerStatus"/> enum.
/// </summary>
public class HandlerStatusTests {
  [Test]
  public async Task HandlerStatus_Success_IsDefinedAsync() {
    var value = HandlerStatus.Success;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task HandlerStatus_Failed_IsDefinedAsync() {
    var value = HandlerStatus.Failed;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task HandlerStatus_EarlyReturn_IsDefinedAsync() {
    var value = HandlerStatus.EarlyReturn;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task HandlerStatus_HasThreeValuesAsync() {
    // Arrange
    var values = Enum.GetValues<HandlerStatus>();

    // Assert
    await Assert.That(values.Length).IsEqualTo(3);
  }

  [Test]
  public async Task HandlerStatus_Success_HasCorrectIntValueAsync() {
    var value = (int)HandlerStatus.Success;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task HandlerStatus_Failed_HasCorrectIntValueAsync() {
    var value = (int)HandlerStatus.Failed;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task HandlerStatus_EarlyReturn_HasCorrectIntValueAsync() {
    var value = (int)HandlerStatus.EarlyReturn;
    await Assert.That(value).IsEqualTo(2);
  }
}
