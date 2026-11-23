// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A message other than an event that a dispatch creates inside a handling (a command sent or invoked locally) takes an
/// id derived from the handling (#2543), so a redelivered handling sends under the ids of its first run.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/SentMessageIdentity.cs</code-under-test>
[Category("Core")]
[Category("Messaging")]
public class SentMessageIdentityTests {
  private const string SERVICE = "orders";
  private const string HANDLER = "PlaceOrderHandler";
  private const string TYPE = "Ns.CreateShipment, Asm";

  private static byte[] _bytes(Guid id) => id.ToByteArray(bigEndian: true);

  [Test]
  public async Task Derive_TheSameInputs_DeriveTheSameIdAsync() {
    var cause = (Guid)TrackedGuid.New();

    await Assert.That(SentMessageIdentity.Derive(cause, SERVICE, HANDLER, TYPE, 2))
      .IsEqualTo(SentMessageIdentity.Derive(cause, SERVICE, HANDLER, TYPE, 2))
      .Because("a redelivered handling sends again in the same order, and must then send under the same ids");
  }

  [Test]
  public async Task Derive_EachInput_SeparatesTheIdsAsync() {
    var cause = (Guid)TrackedGuid.New();
    var baseline = SentMessageIdentity.Derive(cause, SERVICE, HANDLER, TYPE, 0);

    await Assert.That(SentMessageIdentity.Derive((Guid)TrackedGuid.New(), SERVICE, HANDLER, TYPE, 0)).IsNotEqualTo(baseline);
    await Assert.That(SentMessageIdentity.Derive(cause, "billing", HANDLER, TYPE, 0)).IsNotEqualTo(baseline)
      .Because("two services handling one message must not send under one id");
    await Assert.That(SentMessageIdentity.Derive(cause, SERVICE, "OtherHandler", TYPE, 0)).IsNotEqualTo(baseline)
      .Because("two handlers of one message must not send under one id");
    await Assert.That(SentMessageIdentity.Derive(cause, SERVICE, null, TYPE, 0)).IsNotEqualTo(baseline);
    await Assert.That(SentMessageIdentity.Derive(cause, SERVICE, HANDLER, "Ns.Other, Asm", 0)).IsNotEqualTo(baseline);
    await Assert.That(SentMessageIdentity.Derive(cause, SERVICE, HANDLER, TYPE, 1)).IsNotEqualTo(baseline)
      .Because("two sends in one handling are two messages");
  }

  [Test]
  public async Task Derive_IsNeitherAnEmissionIdNorAGeneratedStreamAsync() {
    var cause = (Guid)TrackedGuid.New();
    var id = SentMessageIdentity.Derive(cause, SERVICE, HANDLER, TYPE, 0);

    await Assert.That(id).IsNotEqualTo(EmissionIdentity.Derive(cause, SERVICE, HANDLER, TYPE, 0))
      .Because("its own canonical prefix keeps it apart from the event id the same inputs derive");
    await Assert.That(id).IsNotEqualTo(GeneratedStreamIdentity.DeriveInHandling(cause, SERVICE, HANDLER, TYPE, 0));
  }

  [Test]
  public async Task Derive_SortsAtTheCauseThenByOrdinalAsync() {
    var cause = (Guid)TrackedGuid.New();

    var first = _bytes(SentMessageIdentity.Derive(cause, SERVICE, HANDLER, TYPE, 0));
    var second = _bytes(SentMessageIdentity.Derive(cause, SERVICE, HANDLER, TYPE, 1));
    var source = _bytes(cause);

    await Assert.That(first.AsSpan(0, 10).SequenceEqual(source.AsSpan(0, 10))).IsTrue()
      .Because("the cause's millisecond and counter place the message with the handling that sent it");
    await Assert.That(first[10]).IsEqualTo((byte)0);
    await Assert.That(second[11] >> 4).IsEqualTo(1)
      .Because("the ordinal follows the cause's prefix, so a handling's sends sort in the order it made them");
  }

  [Test]
  public async Task Derive_IsAVersion7IdWithTheRfcVariantAsync() {
    var id = _bytes(SentMessageIdentity.Derive(Guid.Parse("5f0c1d2e-3a4b-4c5d-9e6f-7a8b9c0d1e2f"), SERVICE, HANDLER, TYPE, 0));

    await Assert.That(id[6] >> 4).IsEqualTo(7);
    await Assert.That(id[8] >> 6).IsEqualTo(0b10);
  }

  [Test]
  public async Task Derive_FixedInputs_DeriveTheCompatibilityValueAsync() {
    // Golden value, computed independently of the code under test: the cause's first 80 bits, the ordinal in the next
    // 12, then SHA-256 of "whizbang.sent-message.in-handling.v1\n{cause:N}\n{service}\n{handler}\n{type}\n{ordinal}" in
    // the last 36 bits (low nibble of hash[0], then hash[1..4]). Changing it changes the id a redelivery across the
    // deploy sends under, which the receiver then takes for a new message.
    var id = SentMessageIdentity.Derive(Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-2c3d4e5f6a7b"), SERVICE, HANDLER, TYPE, 3);

    await Assert.That(id).IsEqualTo(Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-00396ad72c23"));
  }

  [Test]
  public async Task Derive_InvalidInputs_AreRefusedAsync() {
    var cause = (Guid)TrackedGuid.New();

    await Assert.That(() => SentMessageIdentity.Derive(Guid.Empty, SERVICE, HANDLER, TYPE, 0)).Throws<ArgumentException>();
    await Assert.That(() => SentMessageIdentity.Derive(cause, null!, HANDLER, TYPE, 0)).Throws<ArgumentNullException>();
    await Assert.That(() => SentMessageIdentity.Derive(cause, SERVICE, HANDLER, null!, 0)).Throws<ArgumentNullException>();
    await Assert.That(() => SentMessageIdentity.Derive(cause, SERVICE, HANDLER, TYPE, -1)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task NextOrdinal_CountsPerHandlingInstance_SoARedeliveryStartsAgainAtZeroAsync() {
    var cause = MessageId.From((Guid)TrackedGuid.New());
    var first = _handling(cause);
    var redelivery = _handling(cause);

    var a = SentMessageIdentity.NextOrdinal(first);
    var b = SentMessageIdentity.NextOrdinal(first);
    var c = SentMessageIdentity.NextOrdinal(redelivery);

    await Assert.That(a).IsEqualTo(0);
    await Assert.That(b).IsEqualTo(1);
    await Assert.That(c).IsEqualTo(0)
      .Because("a redelivery materializes a fresh envelope and must derive the same sequence again");
  }

  [Test]
  public async Task NextOrdinal_NoHandling_IsRefusedAsync() {
    await Assert.That(() => SentMessageIdentity.NextOrdinal(null!)).Throws<ArgumentNullException>();
  }

  private static MessageEnvelope<object> _handling(MessageId id) => new() {
    MessageId = id,
    Payload = "origin",
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };
}
