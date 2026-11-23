// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A <c>[GenerateStreamId]</c> message starts a stream whose id derives from the message's own id (#2274), so a retry
/// of a handling, which re-derives the message's id, also re-derives the stream it starts instead of starting another.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/GeneratedStreamIdentity.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/DerivedIdentity.cs</code-under-test>
[Category("Core")]
[Category("Messaging")]
public class GeneratedStreamIdentityTests {
  private static byte[] _bytes(Guid id) => id.ToByteArray(bigEndian: true);

  [Test]
  public async Task Derive_TheSameMessageId_DerivesTheSameStreamIdAsync() {
    var messageId = (Guid)TrackedGuid.New();

    await Assert.That(GeneratedStreamIdentity.Derive(messageId)).IsEqualTo(GeneratedStreamIdentity.Derive(messageId))
      .Because("a retry re-derives the message's id, and must then start the same stream");
  }

  [Test]
  public async Task Derive_TwoMessages_DeriveTwoStreamsAsync() {
    var first = (Guid)TrackedGuid.New();
    var second = (Guid)TrackedGuid.New();

    await Assert.That(GeneratedStreamIdentity.Derive(second)).IsNotEqualTo(GeneratedStreamIdentity.Derive(first));
  }

  [Test]
  public async Task Derive_IsNotTheMessageIdItselfAsync() {
    var messageId = (Guid)TrackedGuid.New();

    await Assert.That(GeneratedStreamIdentity.Derive(messageId)).IsNotEqualTo(messageId)
      .Because("a stream id and an event id are different things; a hash of the message id keeps them apart");
  }

  [Test]
  public async Task Derive_KeepsTheMessageIdsFirst92Bits_SoTheStreamSortsWithTheMessageThatStartedItAsync() {
    var messageId = EmissionIdentity.Derive((Guid)TrackedGuid.New(), "orders", "Handler", "Ns.Created, Asm", 7);

    var streamId = _bytes(GeneratedStreamIdentity.Derive(messageId));
    var source = _bytes(messageId);

    await Assert.That(streamId.AsSpan(0, 11).SequenceEqual(source.AsSpan(0, 11))).IsTrue()
      .Because("the millisecond, the counter and the ordinal place the message; the stream it starts sorts there too");
    await Assert.That(streamId[11] & 0xF0).IsEqualTo(source[11] & 0xF0)
      .Because("the ordinal's last four bits are the high half of byte 11");
  }

  [Test]
  public async Task Derive_IsAVersion7IdWithTheRfcVariantAsync() {
    // A version 4 message id: the derived stream id must still read as a time-ordered id.
    var streamId = _bytes(GeneratedStreamIdentity.Derive(Guid.Parse("5f0c1d2e-3a4b-4c5d-9e6f-7a8b9c0d1e2f")));

    await Assert.That(streamId[6] >> 4).IsEqualTo(7);
    await Assert.That(streamId[8] >> 6).IsEqualTo(0b10);
  }

  [Test]
  public async Task Derive_AFixedMessageId_DerivesTheCompatibilityValueAsync() {
    // Golden value, computed independently of the code under test: the message id's first 92 bits, then SHA-256 of
    // "whizbang.generated-stream.v1\n{messageId:N}" in the last 36 bits (low nibble of hash[0], then hash[1..4]).
    // A stored stream id is referenced everywhere; changing this value changes the stream a retry starts.
    var streamId = GeneratedStreamIdentity.Derive(Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-2c3d4e5f6a7b"));

    await Assert.That(streamId).IsEqualTo(Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-2c3adb233fe3"));
  }

  [Test]
  public async Task Derive_AnEmptyMessageId_IsRefusedAsync() {
    await Assert.That(() => GeneratedStreamIdentity.Derive(Guid.Empty)).Throws<ArgumentException>()
      .Because("every message has an id; an empty one means the caller has none to derive from");
  }
}
