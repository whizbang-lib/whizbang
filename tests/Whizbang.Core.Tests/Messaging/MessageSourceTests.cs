using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="MessageSource"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Messaging/MessageSource.cs</tests>
public class MessageSourceTests {
  [Test]
  public async Task MessageSource_Local_IsDefinedAsync() {
    var value = MessageSource.Local;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task MessageSource_Outbox_IsDefinedAsync() {
    var value = MessageSource.Outbox;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task MessageSource_Inbox_IsDefinedAsync() {
    var value = MessageSource.Inbox;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task MessageSource_HasThreeValuesAsync() {
    var values = Enum.GetValues<MessageSource>();
    await Assert.That(values.Length).IsEqualTo(3);
  }

  [Test]
  public async Task MessageSource_Local_HasCorrectIntValueAsync() {
    var value = (int)MessageSource.Local;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task MessageSource_Outbox_HasCorrectIntValueAsync() {
    var value = (int)MessageSource.Outbox;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task MessageSource_Inbox_HasCorrectIntValueAsync() {
    var value = (int)MessageSource.Inbox;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task MessageSource_Local_IsDefaultAsync() {
    var value = default(MessageSource);
    await Assert.That(value).IsEqualTo(MessageSource.Local);
  }
}
