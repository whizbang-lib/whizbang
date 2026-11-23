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

  // ---- DeriveInHandling: a message sent inside a handling under a minted id (#2537) ----

  private const string SERVICE = "orders";
  private const string HANDLER = "PlaceOrderHandler";
  private const string TYPE = "Ns.CreateShipment, Asm";

  [Test]
  public async Task DeriveInHandling_TheSameInputs_DeriveTheSameStreamIdAsync() {
    var cause = (Guid)TrackedGuid.New();

    await Assert.That(GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, HANDLER, TYPE, 2))
      .IsEqualTo(GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, HANDLER, TYPE, 2))
      .Because("a redelivered handling sends again in the same order, and must then start the same streams");
  }

  [Test]
  public async Task DeriveInHandling_EachInput_SeparatesTheStreamsAsync() {
    var cause = (Guid)TrackedGuid.New();
    var baseline = GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, HANDLER, TYPE, 0);

    await Assert.That(GeneratedStreamIdentity.DeriveInHandling((Guid)TrackedGuid.New(), SERVICE, HANDLER, TYPE, 0)).IsNotEqualTo(baseline)
      .Because("another handled message is another handling");
    await Assert.That(GeneratedStreamIdentity.DeriveInHandling(cause, "billing", HANDLER, TYPE, 0)).IsNotEqualTo(baseline)
      .Because("two services handling one message must not start one stream");
    await Assert.That(GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, "OtherHandler", TYPE, 0)).IsNotEqualTo(baseline)
      .Because("two handlers of one message must not start one stream");
    await Assert.That(GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, null, TYPE, 0)).IsNotEqualTo(baseline);
    await Assert.That(GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, HANDLER, "Ns.Other, Asm", 0)).IsNotEqualTo(baseline);
    await Assert.That(GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, HANDLER, TYPE, 1)).IsNotEqualTo(baseline)
      .Because("two sends in one handling start two streams");
  }

  [Test]
  public async Task DeriveInHandling_IsNeitherTheCausesStreamNorAnEmissionIdAsync() {
    var cause = (Guid)TrackedGuid.New();
    var streamId = GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, HANDLER, TYPE, 0);

    await Assert.That(streamId).IsNotEqualTo(GeneratedStreamIdentity.Derive(cause));
    await Assert.That(streamId).IsNotEqualTo(EmissionIdentity.Derive(cause, SERVICE, HANDLER, TYPE, 0))
      .Because("its own canonical prefix keeps it apart from the event id the same inputs derive");
  }

  [Test]
  public async Task DeriveInHandling_SortsAtTheCauseThenByOrdinalAsync() {
    var cause = (Guid)TrackedGuid.New();

    var first = _bytes(GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, HANDLER, TYPE, 0));
    var second = _bytes(GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, HANDLER, TYPE, 1));
    var source = _bytes(cause);

    await Assert.That(first.AsSpan(0, 10).SequenceEqual(source.AsSpan(0, 10))).IsTrue()
      .Because("the cause's millisecond and counter place the stream with the handling that started it");
    await Assert.That(first[10]).IsEqualTo((byte)0);
    await Assert.That(second[11] >> 4).IsEqualTo(1)
      .Because("the ordinal follows the cause's prefix, so a handling's streams sort in the order it sent them");
  }

  [Test]
  public async Task DeriveInHandling_IsAVersion7IdWithTheRfcVariantAsync() {
    var streamId = _bytes(GeneratedStreamIdentity.DeriveInHandling(Guid.Parse("5f0c1d2e-3a4b-4c5d-9e6f-7a8b9c0d1e2f"), SERVICE, HANDLER, TYPE, 0));

    await Assert.That(streamId[6] >> 4).IsEqualTo(7);
    await Assert.That(streamId[8] >> 6).IsEqualTo(0b10);
  }

  [Test]
  public async Task DeriveInHandling_FixedInputs_DeriveTheCompatibilityValueAsync() {
    // Golden value, computed independently of the code under test: the cause's first 80 bits, the ordinal in the next
    // 12, then SHA-256 of "whizbang.generated-stream.in-handling.v1\n{cause:N}\n{service}\n{handler}\n{type}\n{ordinal}"
    // in the last 36 bits (low nibble of hash[0], then hash[1..4]). Changing it changes the stream a redelivery across
    // the deploy starts.
    var streamId = GeneratedStreamIdentity.DeriveInHandling(Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-2c3d4e5f6a7b"), SERVICE, HANDLER, TYPE, 3);

    await Assert.That(streamId).IsEqualTo(Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-0039b857114e"));
  }

  [Test]
  public async Task DeriveInHandling_InvalidInputs_AreRefusedAsync() {
    var cause = (Guid)TrackedGuid.New();

    await Assert.That(() => GeneratedStreamIdentity.DeriveInHandling(Guid.Empty, SERVICE, HANDLER, TYPE, 0)).Throws<ArgumentException>();
    await Assert.That(() => GeneratedStreamIdentity.DeriveInHandling(cause, null!, HANDLER, TYPE, 0)).Throws<ArgumentNullException>();
    await Assert.That(() => GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, HANDLER, null!, 0)).Throws<ArgumentNullException>();
    await Assert.That(() => GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, HANDLER, TYPE, -1)).Throws<ArgumentOutOfRangeException>();
  }
}
