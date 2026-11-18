// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>
/// The generated, reflection-free registry of message-type <see cref="DefaultRoutingAttribute"/>s (#1395).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/MessageDefaultRouting.cs</code-under-test>
/// <docs>fundamentals/dispatcher/message-cascade#routed-message-cascading</docs>
public class MessageDefaultRoutingTests {
  public sealed record RegisteredOnly;

  public sealed record NeverRegistered;

  public sealed record RegisteredTwice;

  [DefaultRouting(DispatchModes.Local)]
  public sealed record AttributedTestEvent([property: StreamId] Guid StreamId = default) : IEvent;

  [Test]
  public async Task Find_RegisteredType_ReturnsItsRoutingAsync() {
    MessageDefaultRouting.Register(typeof(RegisteredOnly), DispatchModes.Outbox);

    await Assert.That(MessageDefaultRouting.Find(typeof(RegisteredOnly))).IsEqualTo(DispatchModes.Outbox);
  }

  [Test]
  public async Task Find_UnregisteredType_ReturnsNullAsync() {
    await Assert.That(MessageDefaultRouting.Find(typeof(NeverRegistered))).IsNull();
  }

  [Test]
  public async Task Register_SameTypeTwice_KeepsTheFirstAsync() {
    MessageDefaultRouting.Register(typeof(RegisteredTwice), DispatchModes.Local);
    MessageDefaultRouting.Register(typeof(RegisteredTwice), DispatchModes.Outbox);

    await Assert.That(MessageDefaultRouting.Find(typeof(RegisteredTwice))).IsEqualTo(DispatchModes.Local)
      .Because("every assembly's generated catalog registers its own types; the first registration wins, as in the catalog union");
  }

  [Test]
  public async Task Find_AttributedTypeInThisAssembly_IsRegisteredByTheGeneratorAsync() {
    await Assert.That(MessageDefaultRouting.Find(typeof(AttributedTestEvent))).IsEqualTo(DispatchModes.Local)
      .Because("the catalog generator's module initializer registers it before any test runs");
  }

  [Test]
  public async Task NullType_ThrowsAsync() {
    await Assert.That(() => MessageDefaultRouting.Register(null!, DispatchModes.Local)).Throws<ArgumentNullException>();
    await Assert.That(() => MessageDefaultRouting.Find(null!)).Throws<ArgumentNullException>();
  }
}
