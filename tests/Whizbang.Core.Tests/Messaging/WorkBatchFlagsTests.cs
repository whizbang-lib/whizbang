using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="WorkBatchFlags"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Messaging/WorkCoordinatorEnums.cs</tests>
public class WorkBatchFlagsTests {
  [Test]
  public async Task WorkBatchFlags_None_IsDefinedAsync() {
    var value = WorkBatchFlags.None;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchFlags_NewlyStored_IsDefinedAsync() {
    var value = WorkBatchFlags.NewlyStored;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchFlags_Orphaned_IsDefinedAsync() {
    var value = WorkBatchFlags.Orphaned;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchFlags_DebugMode_IsDefinedAsync() {
    var value = WorkBatchFlags.DebugMode;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchFlags_FromEventStore_IsDefinedAsync() {
    var value = WorkBatchFlags.FromEventStore;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchFlags_HighPriority_IsDefinedAsync() {
    var value = WorkBatchFlags.HighPriority;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchFlags_RetryAfterFailure_IsDefinedAsync() {
    var value = WorkBatchFlags.RetryAfterFailure;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task WorkBatchFlags_HasSevenValuesAsync() {
    var values = Enum.GetValues<WorkBatchFlags>();
    await Assert.That(values.Length).IsEqualTo(7);
  }

  [Test]
  public async Task WorkBatchFlags_None_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchFlags.None;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task WorkBatchFlags_NewlyStored_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchFlags.NewlyStored;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task WorkBatchFlags_Orphaned_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchFlags.Orphaned;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task WorkBatchFlags_DebugMode_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchFlags.DebugMode;
    await Assert.That(value).IsEqualTo(4);
  }

  [Test]
  public async Task WorkBatchFlags_FromEventStore_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchFlags.FromEventStore;
    await Assert.That(value).IsEqualTo(8);
  }

  [Test]
  public async Task WorkBatchFlags_HighPriority_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchFlags.HighPriority;
    await Assert.That(value).IsEqualTo(16);
  }

  [Test]
  public async Task WorkBatchFlags_RetryAfterFailure_HasCorrectIntValueAsync() {
    var value = (int)WorkBatchFlags.RetryAfterFailure;
    await Assert.That(value).IsEqualTo(32);
  }

  [Test]
  public async Task WorkBatchFlags_None_IsDefaultAsync() {
    var value = default(WorkBatchFlags);
    await Assert.That(value).IsEqualTo(WorkBatchFlags.None);
  }

  [Test]
  public async Task WorkBatchFlags_IsFlagsEnumAsync() {
    var flagsAttrs = typeof(WorkBatchFlags).GetCustomAttributes(typeof(FlagsAttribute), false);
    await Assert.That(flagsAttrs.Length).IsGreaterThan(0);
  }

  [Test]
  public async Task WorkBatchFlags_CanCombineFlagsAsync() {
    var combined = WorkBatchFlags.NewlyStored | WorkBatchFlags.HighPriority;
    var intValue = (int)combined;
    await Assert.That(intValue).IsEqualTo(17); // 1 | 16 = 17
  }

  [Test]
  public async Task WorkBatchFlags_HasFlagWorksCorrectlyAsync() {
    var combined = WorkBatchFlags.NewlyStored | WorkBatchFlags.Orphaned | WorkBatchFlags.DebugMode;
    await Assert.That(combined.HasFlag(WorkBatchFlags.NewlyStored)).IsTrue();
    await Assert.That(combined.HasFlag(WorkBatchFlags.Orphaned)).IsTrue();
    await Assert.That(combined.HasFlag(WorkBatchFlags.DebugMode)).IsTrue();
    await Assert.That(combined.HasFlag(WorkBatchFlags.FromEventStore)).IsFalse();
    await Assert.That(combined.HasFlag(WorkBatchFlags.HighPriority)).IsFalse();
    await Assert.That(combined.HasFlag(WorkBatchFlags.RetryAfterFailure)).IsFalse();
  }

  [Test]
  public async Task WorkBatchFlags_ValuesBitShiftedCorrectlyAsync() {
    var newlyStored = (int)WorkBatchFlags.NewlyStored;
    var orphaned = (int)WorkBatchFlags.Orphaned;
    var debugMode = (int)WorkBatchFlags.DebugMode;
    var fromEventStore = (int)WorkBatchFlags.FromEventStore;
    var highPriority = (int)WorkBatchFlags.HighPriority;
    var retryAfterFailure = (int)WorkBatchFlags.RetryAfterFailure;

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
