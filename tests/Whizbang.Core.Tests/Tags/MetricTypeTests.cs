using TUnit.Core;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="MetricType"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Tags/MetricType.cs</tests>
public class MetricTypeTests {
  [Test]
  public async Task MetricType_Counter_IsDefinedAsync() {
    var value = MetricType.Counter;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task MetricType_Histogram_IsDefinedAsync() {
    var value = MetricType.Histogram;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task MetricType_Gauge_IsDefinedAsync() {
    var value = MetricType.Gauge;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task MetricType_HasThreeValuesAsync() {
    var values = Enum.GetValues<MetricType>();
    await Assert.That(values.Length).IsEqualTo(3);
  }

  [Test]
  public async Task MetricType_Counter_HasCorrectIntValueAsync() {
    var value = (int)MetricType.Counter;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task MetricType_Histogram_HasCorrectIntValueAsync() {
    var value = (int)MetricType.Histogram;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task MetricType_Gauge_HasCorrectIntValueAsync() {
    var value = (int)MetricType.Gauge;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task MetricType_Counter_IsDefaultAsync() {
    var value = default(MetricType);
    await Assert.That(value).IsEqualTo(MetricType.Counter);
  }
}
