using TUnit.Core;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="NotificationPriority"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Tags/NotificationPriority.cs</tests>
public class NotificationPriorityTests {
  [Test]
  public async Task NotificationPriority_Low_IsDefinedAsync() {
    var value = NotificationPriority.Low;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task NotificationPriority_Normal_IsDefinedAsync() {
    var value = NotificationPriority.Normal;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task NotificationPriority_High_IsDefinedAsync() {
    var value = NotificationPriority.High;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task NotificationPriority_Critical_IsDefinedAsync() {
    var value = NotificationPriority.Critical;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task NotificationPriority_HasFourValuesAsync() {
    var values = Enum.GetValues<NotificationPriority>();
    await Assert.That(values.Length).IsEqualTo(4);
  }

  [Test]
  public async Task NotificationPriority_Low_HasCorrectIntValueAsync() {
    var value = (int)NotificationPriority.Low;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task NotificationPriority_Normal_HasCorrectIntValueAsync() {
    var value = (int)NotificationPriority.Normal;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task NotificationPriority_High_HasCorrectIntValueAsync() {
    var value = (int)NotificationPriority.High;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task NotificationPriority_Critical_HasCorrectIntValueAsync() {
    var value = (int)NotificationPriority.Critical;
    await Assert.That(value).IsEqualTo(3);
  }

  [Test]
  public async Task NotificationPriority_Low_IsDefaultAsync() {
    var value = default(NotificationPriority);
    await Assert.That(value).IsEqualTo(NotificationPriority.Low);
  }

  [Test]
  public async Task NotificationPriority_PriorityOrder_IncreasesCorrectlyAsync() {
    var low = (int)NotificationPriority.Low;
    var normal = (int)NotificationPriority.Normal;
    var high = (int)NotificationPriority.High;
    var critical = (int)NotificationPriority.Critical;

    await Assert.That(normal).IsGreaterThan(low);
    await Assert.That(high).IsGreaterThan(normal);
    await Assert.That(critical).IsGreaterThan(high);
  }
}
