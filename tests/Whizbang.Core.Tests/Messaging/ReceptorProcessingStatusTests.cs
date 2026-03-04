using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="ReceptorProcessingStatus"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Messaging/ReceptorProcessingStatus.cs</tests>
public class ReceptorProcessingStatusTests {
  [Test]
  public async Task ReceptorProcessingStatus_None_IsDefinedAsync() {
    var value = ReceptorProcessingStatus.None;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task ReceptorProcessingStatus_Processing_IsDefinedAsync() {
    var value = ReceptorProcessingStatus.Processing;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task ReceptorProcessingStatus_Completed_IsDefinedAsync() {
    var value = ReceptorProcessingStatus.Completed;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task ReceptorProcessingStatus_Failed_IsDefinedAsync() {
    var value = ReceptorProcessingStatus.Failed;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task ReceptorProcessingStatus_HasFourValuesAsync() {
    var values = Enum.GetValues<ReceptorProcessingStatus>();
    await Assert.That(values.Length).IsEqualTo(4);
  }

  [Test]
  public async Task ReceptorProcessingStatus_None_HasCorrectIntValueAsync() {
    var value = (int)ReceptorProcessingStatus.None;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task ReceptorProcessingStatus_Processing_HasCorrectIntValueAsync() {
    var value = (int)ReceptorProcessingStatus.Processing;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task ReceptorProcessingStatus_Completed_HasCorrectIntValueAsync() {
    var value = (int)ReceptorProcessingStatus.Completed;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task ReceptorProcessingStatus_Failed_HasCorrectIntValueAsync() {
    var value = (int)ReceptorProcessingStatus.Failed;
    await Assert.That(value).IsEqualTo(4);
  }

  [Test]
  public async Task ReceptorProcessingStatus_None_IsDefaultAsync() {
    var value = default(ReceptorProcessingStatus);
    await Assert.That(value).IsEqualTo(ReceptorProcessingStatus.None);
  }

  [Test]
  public async Task ReceptorProcessingStatus_IsFlagsEnumAsync() {
    var flagsAttrs = typeof(ReceptorProcessingStatus).GetCustomAttributes(typeof(FlagsAttribute), false);
    await Assert.That(flagsAttrs.Length).IsGreaterThan(0);
  }

  [Test]
  public async Task ReceptorProcessingStatus_CanCombineFlagsAsync() {
    var combined = ReceptorProcessingStatus.Processing | ReceptorProcessingStatus.Failed;
    var intValue = (int)combined;
    await Assert.That(intValue).IsEqualTo(5); // 1 | 4 = 5
  }

  [Test]
  public async Task ReceptorProcessingStatus_HasFlagWorksCorrectlyAsync() {
    var combined = ReceptorProcessingStatus.Processing | ReceptorProcessingStatus.Failed;
    await Assert.That(combined.HasFlag(ReceptorProcessingStatus.Processing)).IsTrue();
    await Assert.That(combined.HasFlag(ReceptorProcessingStatus.Failed)).IsTrue();
    await Assert.That(combined.HasFlag(ReceptorProcessingStatus.Completed)).IsFalse();
  }
}
