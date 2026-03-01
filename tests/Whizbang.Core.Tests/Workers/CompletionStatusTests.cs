using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="CompletionStatus"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Workers/CompletionStatus.cs</tests>
public class CompletionStatusTests {
  [Test]
  public async Task CompletionStatus_Pending_IsDefinedAsync() {
    var value = CompletionStatus.Pending;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task CompletionStatus_Sent_IsDefinedAsync() {
    var value = CompletionStatus.Sent;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task CompletionStatus_Acknowledged_IsDefinedAsync() {
    var value = CompletionStatus.Acknowledged;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task CompletionStatus_HasThreeValuesAsync() {
    var values = Enum.GetValues<CompletionStatus>();
    await Assert.That(values.Length).IsEqualTo(3);
  }

  [Test]
  public async Task CompletionStatus_Pending_HasCorrectIntValueAsync() {
    var value = (int)CompletionStatus.Pending;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task CompletionStatus_Sent_HasCorrectIntValueAsync() {
    var value = (int)CompletionStatus.Sent;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task CompletionStatus_Acknowledged_HasCorrectIntValueAsync() {
    var value = (int)CompletionStatus.Acknowledged;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task CompletionStatus_Pending_IsDefaultAsync() {
    var value = default(CompletionStatus);
    await Assert.That(value).IsEqualTo(CompletionStatus.Pending);
  }

  [Test]
  public async Task CompletionStatus_TransitionOrder_IsCorrectAsync() {
    // Verify the expected state transition order: Pending → Sent → Acknowledged
    var pending = (int)CompletionStatus.Pending;
    var sent = (int)CompletionStatus.Sent;
    var acknowledged = (int)CompletionStatus.Acknowledged;

    await Assert.That(sent).IsGreaterThan(pending);
    await Assert.That(acknowledged).IsGreaterThan(sent);
  }
}
