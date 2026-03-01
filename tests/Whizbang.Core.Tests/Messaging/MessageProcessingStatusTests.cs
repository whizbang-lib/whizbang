using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="MessageProcessingStatus"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Messaging/WorkCoordinatorEnums.cs</tests>
public class MessageProcessingStatusTests {
  [Test]
  public async Task MessageProcessingStatus_None_IsDefinedAsync() {
    var value = MessageProcessingStatus.None;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task MessageProcessingStatus_Stored_IsDefinedAsync() {
    var value = MessageProcessingStatus.Stored;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task MessageProcessingStatus_EventStored_IsDefinedAsync() {
    var value = MessageProcessingStatus.EventStored;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task MessageProcessingStatus_Published_IsDefinedAsync() {
    var value = MessageProcessingStatus.Published;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task MessageProcessingStatus_Failed_IsDefinedAsync() {
    var value = MessageProcessingStatus.Failed;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task MessageProcessingStatus_HasFiveValuesAsync() {
    var values = Enum.GetValues<MessageProcessingStatus>();
    await Assert.That(values.Length).IsEqualTo(5);
  }

  [Test]
  public async Task MessageProcessingStatus_None_HasCorrectIntValueAsync() {
    var value = (int)MessageProcessingStatus.None;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task MessageProcessingStatus_Stored_HasCorrectIntValueAsync() {
    var value = (int)MessageProcessingStatus.Stored;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task MessageProcessingStatus_EventStored_HasCorrectIntValueAsync() {
    var value = (int)MessageProcessingStatus.EventStored;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task MessageProcessingStatus_Published_HasCorrectIntValueAsync() {
    var value = (int)MessageProcessingStatus.Published;
    await Assert.That(value).IsEqualTo(4);
  }

  [Test]
  public async Task MessageProcessingStatus_Failed_HasCorrectIntValueAsync() {
    var value = (int)MessageProcessingStatus.Failed;
    await Assert.That(value).IsEqualTo(32768); // 1 << 15
  }

  [Test]
  public async Task MessageProcessingStatus_None_IsDefaultAsync() {
    var value = default(MessageProcessingStatus);
    await Assert.That(value).IsEqualTo(MessageProcessingStatus.None);
  }

  [Test]
  public async Task MessageProcessingStatus_IsFlagsEnumAsync() {
    var flagsAttrs = typeof(MessageProcessingStatus).GetCustomAttributes(typeof(FlagsAttribute), false);
    await Assert.That(flagsAttrs.Length).IsGreaterThan(0);
  }

  [Test]
  public async Task MessageProcessingStatus_CanCombineStoredAndPublishedAsync() {
    var combined = MessageProcessingStatus.Stored | MessageProcessingStatus.Published;
    var intValue = (int)combined;
    await Assert.That(intValue).IsEqualTo(5); // 1 | 4 = 5
  }

  [Test]
  public async Task MessageProcessingStatus_CanCombineWithFailedAsync() {
    var combined = MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored | MessageProcessingStatus.Failed;
    var intValue = (int)combined;
    await Assert.That(intValue).IsEqualTo(32771); // 1 | 2 | 32768 = 32771
  }

  [Test]
  public async Task MessageProcessingStatus_HasFlagWorksCorrectlyAsync() {
    var combined = MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored | MessageProcessingStatus.Failed;
    await Assert.That(combined.HasFlag(MessageProcessingStatus.Stored)).IsTrue();
    await Assert.That(combined.HasFlag(MessageProcessingStatus.EventStored)).IsTrue();
    await Assert.That(combined.HasFlag(MessageProcessingStatus.Failed)).IsTrue();
    await Assert.That(combined.HasFlag(MessageProcessingStatus.Published)).IsFalse();
  }

  [Test]
  public async Task MessageProcessingStatus_ValuesBitShiftedCorrectlyAsync() {
    var stored = (int)MessageProcessingStatus.Stored;
    var eventStored = (int)MessageProcessingStatus.EventStored;
    var published = (int)MessageProcessingStatus.Published;
    var failed = (int)MessageProcessingStatus.Failed;

    var bit0 = 1 << 0;
    var bit1 = 1 << 1;
    var bit2 = 1 << 2;
    var bit15 = 1 << 15;

    await Assert.That(stored).IsEqualTo(bit0);
    await Assert.That(eventStored).IsEqualTo(bit1);
    await Assert.That(published).IsEqualTo(bit2);
    await Assert.That(failed).IsEqualTo(bit15);
  }

  [Test]
  public async Task MessageProcessingStatus_TypicalOutboxSuccess_HasCorrectFlagsAsync() {
    // Typical outbox success: Stored + Published
    var status = MessageProcessingStatus.Stored | MessageProcessingStatus.Published;
    await Assert.That(status.HasFlag(MessageProcessingStatus.Stored)).IsTrue();
    await Assert.That(status.HasFlag(MessageProcessingStatus.Published)).IsTrue();
    await Assert.That(status.HasFlag(MessageProcessingStatus.Failed)).IsFalse();
  }

  [Test]
  public async Task MessageProcessingStatus_TypicalEventSuccess_HasCorrectFlagsAsync() {
    // Typical event success: Stored + EventStored + Published
    var status = MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored | MessageProcessingStatus.Published;
    await Assert.That(status.HasFlag(MessageProcessingStatus.Stored)).IsTrue();
    await Assert.That(status.HasFlag(MessageProcessingStatus.EventStored)).IsTrue();
    await Assert.That(status.HasFlag(MessageProcessingStatus.Published)).IsTrue();
    await Assert.That(status.HasFlag(MessageProcessingStatus.Failed)).IsFalse();
  }
}
