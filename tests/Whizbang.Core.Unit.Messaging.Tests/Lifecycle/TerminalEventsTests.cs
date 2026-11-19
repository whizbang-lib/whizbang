// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Lifecycle;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// The generated, reflection-free registry of <see cref="TerminalEventAttribute">terminal</see> event types (WS-3 S4).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Lifecycle/TerminalEvents.cs</code-under-test>
/// <docs>fundamentals/events/stream-end-of-life#terminal-events</docs>
public class TerminalEventsTests {
  public sealed record RegisteredOnly;

  public sealed record NeverRegistered;

  [TerminalEvent]
  public sealed record AttributedTerminalEvent([property: StreamId] Guid StreamId = default) : IEvent;

  [TerminalEvent]
  public record AttributedBaseEvent([property: StreamId] Guid StreamId = default) : IEvent;

  public sealed record DerivedFromATerminalEvent : AttributedBaseEvent;

  public sealed record PlainEvent([property: StreamId] Guid StreamId = default) : IEvent;

  [Test]
  public async Task IsTerminal_ARegisteredType_IsTrue_AndRegisteringItAgainChangesNothingAsync() {
    TerminalEvents.Register(typeof(RegisteredOnly));
    TerminalEvents.Register(typeof(RegisteredOnly));

    await Assert.That(TerminalEvents.IsTerminal(typeof(RegisteredOnly))).IsTrue();
  }

  [Test]
  public async Task IsTerminal_AnUnregisteredType_IsFalseAsync() =>
    await Assert.That(TerminalEvents.IsTerminal(typeof(NeverRegistered))).IsFalse();

  [Test]
  public async Task IsTerminal_AnAttributedTypeInThisAssembly_IsRegisteredByTheGeneratorAsync() {
    await Assert.That(TerminalEvents.IsTerminal(typeof(AttributedTerminalEvent))).IsTrue()
      .Because("the catalog generator's module initializer registers it before any test runs");
    await Assert.That(TerminalEvents.IsTerminal(typeof(DerivedFromATerminalEvent))).IsTrue()
      .Because("the attribute is inherited, so an event derived from a terminal event is terminal too");
    await Assert.That(TerminalEvents.IsTerminal(typeof(PlainEvent))).IsFalse();
  }

  [Test]
  public async Task NullType_ThrowsAsync() {
    await Assert.That(() => TerminalEvents.Register(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => TerminalEvents.IsTerminal(null!)).Throws<ArgumentNullException>();
  }
}
