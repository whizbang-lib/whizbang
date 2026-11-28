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
/// The anchor a receptor invoked locally from inside another receptor's handling derives its own handling's ids from
/// (#2633): one id per local invocation, on the outer handling's local-invocation ordinals.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/LocalInvocationIdentity.cs</code-under-test>
[Category("Core")]
[Category("Messaging")]
public class LocalInvocationIdentityTests {
  private const string SERVICE = "orders";
  private const string HANDLER = "PlaceOrderHandler";
  private const string RECEPTOR = "Ns.PlaceOrderReceptor";

  [Test]
  public async Task Derive_TheLayout_IsTheDocumentedCanonicalStringAtTheOuterAnchorAsync() {
    var anchor = (Guid)TrackedGuid.New();

    var id = LocalInvocationIdentity.Derive(anchor, SERVICE, HANDLER, RECEPTOR, 3);

    // A compatibility contract: a retry across a deploy must derive the same anchor, so the canonical string is pinned
    var canonical = $"whizbang.local-invoke.in-handling.v1\n{anchor:N}\n{SERVICE}\n{HANDLER}\n{RECEPTOR}\n3";
    await Assert.That(id).IsEqualTo(DerivedIdentity.FromCanonical(anchor, 3, canonical));
  }

  [Test]
  public async Task Derive_FixedInputs_DeriveTheCompatibilityValueAsync() {
    // A compatibility contract: every id the inner receptor derives starts from this anchor, so a layout change would make
    // a retry across the deploy derive different ids. Computed by an independent reference of the layout (SHA-256 over
    // the canonical string, written into the shared derived layout), not by this implementation.
    var id = LocalInvocationIdentity.Derive(Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-2c3d4e5f6a7b"), SERVICE, HANDLER, RECEPTOR, 3);

    await Assert.That(id).IsEqualTo(Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-003a70ec10d0"));
  }

  [Test]
  public async Task Derive_ANullHandlerAndReceptor_AreEmptyInTheCanonicalStringAsync() {
    var anchor = (Guid)TrackedGuid.New();

    var id = LocalInvocationIdentity.Derive(anchor, SERVICE, null, null, 0);

    await Assert.That(id).IsEqualTo(DerivedIdentity.FromCanonical(anchor, 0,
      $"whizbang.local-invoke.in-handling.v1\n{anchor:N}\n{SERVICE}\n\n\n0"));
  }

  [Test]
  public async Task Derive_EachInput_SeparatesTheAnchorsAsync() {
    var anchor = (Guid)TrackedGuid.New();
    var baseline = LocalInvocationIdentity.Derive(anchor, SERVICE, HANDLER, RECEPTOR, 0);

    await Assert.That(LocalInvocationIdentity.Derive((Guid)TrackedGuid.New(), SERVICE, HANDLER, RECEPTOR, 0)).IsNotEqualTo(baseline);
    await Assert.That(LocalInvocationIdentity.Derive(anchor, "billing", HANDLER, RECEPTOR, 0)).IsNotEqualTo(baseline);
    await Assert.That(LocalInvocationIdentity.Derive(anchor, SERVICE, "Other", RECEPTOR, 0)).IsNotEqualTo(baseline);
    await Assert.That(LocalInvocationIdentity.Derive(anchor, SERVICE, HANDLER, "Ns.Other", 0)).IsNotEqualTo(baseline);
    await Assert.That(LocalInvocationIdentity.Derive(anchor, SERVICE, HANDLER, RECEPTOR, 1)).IsNotEqualTo(baseline)
      .Because("two local invocations in one handling are two handlings");
    await Assert.That(baseline).IsNotEqualTo(SentMessageIdentity.Derive(anchor, SERVICE, HANDLER, RECEPTOR, 0))
      .Because("its own canonical prefix keeps it apart from a sent message's id on the same inputs");
  }

  [Test]
  public async Task Derive_InvalidInputs_ThrowAsync() {
    var anchor = (Guid)TrackedGuid.New();

    await Assert.That(() => LocalInvocationIdentity.Derive(Guid.Empty, SERVICE, HANDLER, RECEPTOR, 0)).Throws<ArgumentException>();
    await Assert.That(() => LocalInvocationIdentity.Derive(anchor, null!, HANDLER, RECEPTOR, 0)).Throws<ArgumentNullException>();
    await Assert.That(() => LocalInvocationIdentity.Derive(anchor, SERVICE, HANDLER, RECEPTOR, -1)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task NextOrdinal_CountsPerHandlingEnvelope_AndIsNotTheSendOrdinalsAsync() {
    var first = _envelope();
    var second = _envelope();

    var a = LocalInvocationIdentity.NextOrdinal(first);
    var send = SentMessageIdentity.NextOrdinal(first);
    var b = LocalInvocationIdentity.NextOrdinal(first);
    var other = LocalInvocationIdentity.NextOrdinal(second);

    await Assert.That(a).IsEqualTo(0);
    await Assert.That(b).IsEqualTo(1)
      .Because("a send between two local invocations takes a send ordinal, not a local-invocation one");
    await Assert.That(send).IsEqualTo(0);
    await Assert.That(other).IsEqualTo(0).Because("a retry materializes a fresh envelope and starts again at zero");
    await Assert.That(() => LocalInvocationIdentity.NextOrdinal(null!)).Throws<ArgumentNullException>();
  }

  internal static MessageEnvelope<object> _envelope(HandlingDerivationScheme? scheme = null) => new() {
    MessageId = MessageId.New(),
    Payload = new object(),
    Hops = [],
    DispatchContext = new MessageDispatchContext {
      Mode = DispatchModes.Outbox,
      Source = MessageSource.Inbox,
      HandlerName = HANDLER,
      DerivationScheme = scheme,
    },
  };
}
