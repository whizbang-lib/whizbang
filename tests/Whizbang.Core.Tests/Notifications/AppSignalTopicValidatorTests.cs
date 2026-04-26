using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications.AppSignals;

namespace Whizbang.Core.Tests.Notifications;

public class AppSignalTopicValidatorTests {

  [Test]
  public async Task Validate_ValidTopic_ReturnsTopicAsync() {
    var result = AppSignalTopicValidator.Validate("user_signup");
    await Assert.That(result).IsEqualTo("user_signup");
  }

  [Test]
  public async Task Validate_EmptyTopic_ThrowsAsync() {
    var threw = false;
    try { AppSignalTopicValidator.Validate(""); } catch (ArgumentException) { threw = true; }
    await Assert.That(threw).IsTrue();
  }

  [Test]
  public async Task Validate_WhPrefix_ThrowsAsync() {
    var threw = false;
    try { AppSignalTopicValidator.Validate("wh_internal"); } catch (ArgumentException ex) {
      threw = ex.Message.Contains("reserved", StringComparison.Ordinal);
    }
    await Assert.That(threw).IsTrue();
  }

  [Test]
  public async Task Validate_UppercaseLetters_ThrowsAsync() {
    var threw = false;
    try { AppSignalTopicValidator.Validate("UserSignup"); } catch (ArgumentException) { threw = true; }
    await Assert.That(threw).IsTrue();
  }

  [Test]
  public async Task Validate_StartsWithDigit_ThrowsAsync() {
    var threw = false;
    try { AppSignalTopicValidator.Validate("1signup"); } catch (ArgumentException) { threw = true; }
    await Assert.That(threw).IsTrue();
  }

  [Test]
  public async Task Validate_TooLong_ThrowsAsync() {
    var threw = false;
    var longTopic = "a" + new string('b', 63);
    try { AppSignalTopicValidator.Validate(longTopic); } catch (ArgumentException) { threw = true; }
    await Assert.That(threw).IsTrue();
  }

  [Test]
  public async Task ToChannelName_ValidTopic_PrependsPrefixAsync() {
    var channel = AppSignalTopicValidator.ToChannelName("user_signup");
    await Assert.That(channel).IsEqualTo("wh_app_user_signup");
  }

  [Test]
  public async Task NoOpAppSignalChannel_PublishValidatesTopicAsync() {
    var channel = new NoOpAppSignalChannel();
    var threw = false;
    try {
      await channel.PublishAsync("wh_internal", "payload");
    } catch (ArgumentException) {
      threw = true;
    }
    await Assert.That(threw).IsTrue();
  }
}
