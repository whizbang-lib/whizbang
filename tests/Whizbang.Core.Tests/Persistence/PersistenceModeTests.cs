using TUnit.Core;
using Whizbang.Core.Persistence;

namespace Whizbang.Core.Tests.Persistence;

/// <summary>
/// Tests for <see cref="PersistenceMode"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Persistence/PersistenceMode.cs</tests>
public class PersistenceModeTests {
  [Test]
  public async Task PersistenceMode_Immediate_IsDefinedAsync() {
    var value = PersistenceMode.Immediate;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task PersistenceMode_Batched_IsDefinedAsync() {
    var value = PersistenceMode.Batched;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task PersistenceMode_Outbox_IsDefinedAsync() {
    var value = PersistenceMode.Outbox;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task PersistenceMode_HasThreeValuesAsync() {
    var values = Enum.GetValues<PersistenceMode>();
    await Assert.That(values.Length).IsEqualTo(3);
  }

  [Test]
  public async Task PersistenceMode_Immediate_HasCorrectIntValueAsync() {
    var value = (int)PersistenceMode.Immediate;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task PersistenceMode_Batched_HasCorrectIntValueAsync() {
    var value = (int)PersistenceMode.Batched;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task PersistenceMode_Outbox_HasCorrectIntValueAsync() {
    var value = (int)PersistenceMode.Outbox;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task PersistenceMode_Immediate_IsDefaultAsync() {
    var value = default(PersistenceMode);
    await Assert.That(value).IsEqualTo(PersistenceMode.Immediate);
  }
}
