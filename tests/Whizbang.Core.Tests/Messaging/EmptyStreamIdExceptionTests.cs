using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Locks the v0.657 invariants around <see cref="EmptyStreamIdException"/> — the typed
/// exception thrown by the storage-time guard under
/// <see cref="Whizbang.Core.Configuration.EmptyStreamIdPolicy.Reject"/>. Carries the
/// offending <c>message_id</c> + <c>message_type</c> so producers see the exact bad row
/// in their stack trace, not a generic "constraint violation."
/// </summary>
/// <docs>operations/configuration/empty-stream-id-policy</docs>
public class EmptyStreamIdExceptionTests {

  [Test]
  public async Task Constructor_CarriesMessageIdAndTypeAsync() {
    Guid messageId = TrackedGuid.NewMedo();
    var messageType = "App.Contracts.Auth.RemoveUserCommand";

    var ex = new EmptyStreamIdException(messageId, messageType);

    await Assert.That(ex.MessageId).IsEqualTo(messageId)
      .Because("Producers debugging via stack trace need the exact message_id of the offending row to look it up in wh_outbox / their write-side context.");
    await Assert.That(ex.MessageType).IsEqualTo(messageType)
      .Because("Telling the producer 'this contract type wrote a zero-UUID stream' is more actionable than 'some message failed' — they search their codebase for the type and find the producer-side bug.");
  }

  [Test]
  public async Task Constructor_MessageContainsBothIdAndTypeAsync() {
    Guid messageId = TrackedGuid.NewMedo();
    var messageType = "Sample.MessageType";

    var ex = new EmptyStreamIdException(messageId, messageType);

    await Assert.That(ex.Message).Contains(messageId.ToString())
      .Because("Stack-trace-only debugging is common; the Message property must self-describe.");
    await Assert.That(ex.Message).Contains(messageType)
      .Because("Same reason — log readers shouldn't need to inspect properties.");
    await Assert.That(ex.Message).Contains("stream_id")
      .Because("Operators reading exception logs need to know WHAT was wrong — 'stream_id' is the searchable column name in wh_outbox / wh_inbox.");
  }
}
