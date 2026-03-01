using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="SyncOutcome"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Perspectives/Sync/SyncOutcome.cs</tests>
public class SyncOutcomeTests {
  [Test]
  public async Task SyncOutcome_Synced_IsDefinedAsync() {
    var value = SyncOutcome.Synced;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SyncOutcome_TimedOut_IsDefinedAsync() {
    var value = SyncOutcome.TimedOut;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SyncOutcome_NoPendingEvents_IsDefinedAsync() {
    var value = SyncOutcome.NoPendingEvents;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task SyncOutcome_HasThreeValuesAsync() {
    var values = Enum.GetValues<SyncOutcome>();
    await Assert.That(values.Length).IsEqualTo(3);
  }

  [Test]
  public async Task SyncOutcome_Synced_HasCorrectIntValueAsync() {
    var value = (int)SyncOutcome.Synced;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task SyncOutcome_TimedOut_HasCorrectIntValueAsync() {
    var value = (int)SyncOutcome.TimedOut;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task SyncOutcome_NoPendingEvents_HasCorrectIntValueAsync() {
    var value = (int)SyncOutcome.NoPendingEvents;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task SyncOutcome_Synced_IsDefaultAsync() {
    var value = default(SyncOutcome);
    await Assert.That(value).IsEqualTo(SyncOutcome.Synced);
  }
}
