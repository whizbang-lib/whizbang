using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="WorkBatchOptions"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Messaging/WorkCoordinatorEnums.cs</tests>
public class WorkBatchOptionsTests {
  [Test]
  public async Task WorkBatchOptions_None_IsDefinedAsync() {
    var value = WorkBatchOptions.None;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchOptions_NewlyStored_IsDefinedAsync() {
    var value = WorkBatchOptions.NewlyStored;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchOptions_Orphaned_IsDefinedAsync() {
    var value = WorkBatchOptions.Orphaned;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchOptions_DebugMode_IsDefinedAsync() {
    var value = WorkBatchOptions.DebugMode;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchOptions_FromEventStore_IsDefinedAsync() {
    var value = WorkBatchOptions.FromEventStore;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchOptions_HighPriority_IsDefinedAsync() {
    var value = WorkBatchOptions.HighPriority;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchOptions_RetryAfterFailure_IsDefinedAsync() {
    var value = WorkBatchOptions.RetryAfterFailure;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchOptions_HasEightValuesAsync() {
    var values = Enum.GetValues<WorkBatchOptions>();
    await Assert.That(values.Length).IsEqualTo(8);
  }

  [Test]
  public async Task WorkBatchOptions_None_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchOptions.None;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task WorkBatchOptions_NewlyStored_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchOptions.NewlyStored;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task WorkBatchOptions_Orphaned_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchOptions.Orphaned;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task WorkBatchOptions_DebugMode_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchOptions.DebugMode;
    await Assert.That(value).IsEqualTo(4);
  }

  [Test]
  public async Task WorkBatchOptions_FromEventStore_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchOptions.FromEventStore;
    await Assert.That(value).IsEqualTo(8);
  }

  [Test]
  public async Task WorkBatchOptions_HighPriority_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchOptions.HighPriority;
    await Assert.That(value).IsEqualTo(16);
  }

  [Test]
  public async Task WorkBatchOptions_RetryAfterFailure_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchOptions.RetryAfterFailure;
    await Assert.That(value).IsEqualTo(32);
  }

  [Test]
  public async Task WorkBatchOptions_None_IsDefaultAsync() {
    var value = default(WorkBatchOptions);
    await Assert.That(value).IsEqualTo(WorkBatchOptions.None);
  }

  [Test]
  public async Task WorkBatchOptions_IsFlagsEnumAsync() {
    var flagsAttrs = typeof(WorkBatchOptions).GetCustomAttributes(typeof(FlagsAttribute), false);
    await Assert.That(flagsAttrs.Length).IsGreaterThan(0);
  }

  [Test]
  public async Task WorkBatchOptions_CanCombineFlagsAsync() {
    var combined = WorkBatchOptions.NewlyStored | WorkBatchOptions.HighPriority;
    var intValue = (int)combined;
    await Assert.That(intValue).IsEqualTo(17); // 1 | 16 = 17
  }

  [Test]
  public async Task WorkBatchOptions_HasFlagWorksCorrectlyAsync() {
    var combined = WorkBatchOptions.NewlyStored | WorkBatchOptions.Orphaned | WorkBatchOptions.DebugMode;
    await Assert.That(combined.HasFlag(WorkBatchOptions.NewlyStored)).IsTrue();
    await Assert.That(combined.HasFlag(WorkBatchOptions.Orphaned)).IsTrue();
    await Assert.That(combined.HasFlag(WorkBatchOptions.DebugMode)).IsTrue();
    await Assert.That(combined.HasFlag(WorkBatchOptions.FromEventStore)).IsFalse();
    await Assert.That(combined.HasFlag(WorkBatchOptions.HighPriority)).IsFalse();
    await Assert.That(combined.HasFlag(WorkBatchOptions.RetryAfterFailure)).IsFalse();
  }

  [Test]
  public async Task WorkBatchOptions_ValuesBitShiftedCorrectlyAsync() {
    var newlyStored = (int)WorkBatchOptions.NewlyStored;
    var orphaned = (int)WorkBatchOptions.Orphaned;
    var debugMode = (int)WorkBatchOptions.DebugMode;
    var fromEventStore = (int)WorkBatchOptions.FromEventStore;
    var highPriority = (int)WorkBatchOptions.HighPriority;
    var retryAfterFailure = (int)WorkBatchOptions.RetryAfterFailure;

    var bit0 = 1 << 0;
    var bit1 = 1 << 1;
    var bit2 = 1 << 2;
    var bit3 = 1 << 3;
    var bit4 = 1 << 4;
    var bit5 = 1 << 5;

    await Assert.That(newlyStored).IsEqualTo(bit0);
    await Assert.That(orphaned).IsEqualTo(bit1);
    await Assert.That(debugMode).IsEqualTo(bit2);
    await Assert.That(fromEventStore).IsEqualTo(bit3);
    await Assert.That(highPriority).IsEqualTo(bit4);
    await Assert.That(retryAfterFailure).IsEqualTo(bit5);
  }
}
