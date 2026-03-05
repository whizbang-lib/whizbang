using TUnit.Core;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="SignalPriority"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Tags/SignalPriority.cs</tests>
public class SignalPriorityTests {
  [Test]
  public async Task SignalPriority_Low_IsDefinedAsync() {
    var value = SignalPriority.Low;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SignalPriority_Normal_IsDefinedAsync() {
    var value = SignalPriority.Normal;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SignalPriority_High_IsDefinedAsync() {
    var value = SignalPriority.High;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SignalPriority_Critical_IsDefinedAsync() {
    var value = SignalPriority.Critical;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SignalPriority_HasFourValuesAsync() {
    var values = Enum.GetValues<SignalPriority>();
    await Assert.That(values.Length).IsEqualTo(4);
  }

  [Test]
  public async Task SignalPriority_Low_HasCorrectIntValueAsync() {
    var value = (int)SignalPriority.Low;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task SignalPriority_Normal_HasCorrectIntValueAsync() {
    var value = (int)SignalPriority.Normal;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task SignalPriority_High_HasCorrectIntValueAsync() {
    var value = (int)SignalPriority.High;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task SignalPriority_Critical_HasCorrectIntValueAsync() {
    var value = (int)SignalPriority.Critical;
    await Assert.That(value).IsEqualTo(3);
  }

  [Test]
  public async Task SignalPriority_Low_IsDefaultAsync() {
    var value = default(SignalPriority);
    await Assert.That(value).IsEqualTo(SignalPriority.Low);
  }

  [Test]
  public async Task SignalPriority_PriorityOrder_IncreasesCorrectlyAsync() {
    var low = (int)SignalPriority.Low;
    var normal = (int)SignalPriority.Normal;
    var high = (int)SignalPriority.High;
    var critical = (int)SignalPriority.Critical;

    await Assert.That(normal).IsGreaterThan(low);
    await Assert.That(high).IsGreaterThan(normal);
    await Assert.That(critical).IsGreaterThan(high);
  }
}
