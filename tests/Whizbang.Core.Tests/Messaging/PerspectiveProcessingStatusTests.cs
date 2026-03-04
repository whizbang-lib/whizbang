using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="PerspectiveProcessingStatus"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Messaging/PerspectiveProcessingStatus.cs</tests>
public class PerspectiveProcessingStatusTests {
  [Test]
  public async Task PerspectiveProcessingStatus_None_IsDefinedAsync() {
    var value = PerspectiveProcessingStatus.None;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task PerspectiveProcessingStatus_Processing_IsDefinedAsync() {
    var value = PerspectiveProcessingStatus.Processing;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task PerspectiveProcessingStatus_Completed_IsDefinedAsync() {
    var value = PerspectiveProcessingStatus.Completed;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task PerspectiveProcessingStatus_Failed_IsDefinedAsync() {
    var value = PerspectiveProcessingStatus.Failed;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task PerspectiveProcessingStatus_CatchingUp_IsDefinedAsync() {
    var value = PerspectiveProcessingStatus.CatchingUp;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task PerspectiveProcessingStatus_HasFiveValuesAsync() {
    var values = Enum.GetValues<PerspectiveProcessingStatus>();
    await Assert.That(values.Length).IsEqualTo(5);
  }

  [Test]
  public async Task PerspectiveProcessingStatus_None_HasCorrectIntValueAsync() {
    var value = (int)PerspectiveProcessingStatus.None;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task PerspectiveProcessingStatus_Processing_HasCorrectIntValueAsync() {
    var value = (int)PerspectiveProcessingStatus.Processing;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task PerspectiveProcessingStatus_Completed_HasCorrectIntValueAsync() {
    var value = (int)PerspectiveProcessingStatus.Completed;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task PerspectiveProcessingStatus_Failed_HasCorrectIntValueAsync() {
    var value = (int)PerspectiveProcessingStatus.Failed;
    await Assert.That(value).IsEqualTo(4);
  }

  [Test]
  public async Task PerspectiveProcessingStatus_CatchingUp_HasCorrectIntValueAsync() {
    var value = (int)PerspectiveProcessingStatus.CatchingUp;
    await Assert.That(value).IsEqualTo(8);
  }

  [Test]
  public async Task PerspectiveProcessingStatus_None_IsDefaultAsync() {
    var value = default(PerspectiveProcessingStatus);
    await Assert.That(value).IsEqualTo(PerspectiveProcessingStatus.None);
  }

  [Test]
  public async Task PerspectiveProcessingStatus_IsFlagsEnumAsync() {
    var flagsAttrs = typeof(PerspectiveProcessingStatus).GetCustomAttributes(typeof(FlagsAttribute), false);
    await Assert.That(flagsAttrs.Length).IsGreaterThan(0);
  }

  [Test]
  public async Task PerspectiveProcessingStatus_CanCombineFlagsAsync() {
    var combined = PerspectiveProcessingStatus.Processing | PerspectiveProcessingStatus.CatchingUp;
    var intValue = (int)combined;
    await Assert.That(intValue).IsEqualTo(9); // 1 | 8 = 9
  }

  [Test]
  public async Task PerspectiveProcessingStatus_HasFlagWorksCorrectlyAsync() {
    var combined = PerspectiveProcessingStatus.Processing | PerspectiveProcessingStatus.Failed;
    await Assert.That(combined.HasFlag(PerspectiveProcessingStatus.Processing)).IsTrue();
    await Assert.That(combined.HasFlag(PerspectiveProcessingStatus.Failed)).IsTrue();
    await Assert.That(combined.HasFlag(PerspectiveProcessingStatus.Completed)).IsFalse();
    await Assert.That(combined.HasFlag(PerspectiveProcessingStatus.CatchingUp)).IsFalse();
  }

  [Test]
  public async Task PerspectiveProcessingStatus_ValuesBitShiftedCorrectlyAsync() {
    // Verify the bit-shifted values (1 << 0, 1 << 1, 1 << 2, 1 << 3)
    var processing = (int)PerspectiveProcessingStatus.Processing;
    var completed = (int)PerspectiveProcessingStatus.Completed;
    var failed = (int)PerspectiveProcessingStatus.Failed;
    var catchingUp = (int)PerspectiveProcessingStatus.CatchingUp;

    var bit0 = 1 << 0;
    var bit1 = 1 << 1;
    var bit2 = 1 << 2;
    var bit3 = 1 << 3;

    await Assert.That(processing).IsEqualTo(bit0);
    await Assert.That(completed).IsEqualTo(bit1);
    await Assert.That(failed).IsEqualTo(bit2);
    await Assert.That(catchingUp).IsEqualTo(bit3);
  }
}
