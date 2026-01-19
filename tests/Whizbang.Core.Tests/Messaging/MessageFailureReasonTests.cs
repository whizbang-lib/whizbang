using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

public class MessageFailureReasonTests {
  [Test]
  public async Task MessageFailureReason_HasExpectedValuesAsync() {
    // TUnitAssertions0005: Intentional enum value regression tests to ensure values don't accidentally change
#pragma warning disable TUnitAssertions0005
    await Assert.That((int)MessageFailureReason.None).IsEqualTo(0);
    await Assert.That((int)MessageFailureReason.TransportNotReady).IsEqualTo(1);
    await Assert.That((int)MessageFailureReason.TransportException).IsEqualTo(2);
    await Assert.That((int)MessageFailureReason.SerializationError).IsEqualTo(3);
    await Assert.That((int)MessageFailureReason.ValidationError).IsEqualTo(4);
    await Assert.That((int)MessageFailureReason.MaxAttemptsExceeded).IsEqualTo(5);
    await Assert.That((int)MessageFailureReason.LeaseExpired).IsEqualTo(6);
    await Assert.That((int)MessageFailureReason.Unknown).IsEqualTo(99);
#pragma warning restore TUnitAssertions0005
  }

  [Test]
  public async Task MessageFailureReason_CanConvertToIntAsync() {
    var reason = MessageFailureReason.TransportNotReady;
    int value = (int)reason;

    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task MessageFailureReason_CanConvertFromIntAsync() {
    int value = 2;
    var reason = (MessageFailureReason)value;

    await Assert.That(reason).IsEqualTo(MessageFailureReason.TransportException);
  }
}
