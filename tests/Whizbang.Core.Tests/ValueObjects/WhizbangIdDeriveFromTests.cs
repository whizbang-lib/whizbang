// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// The generated <c>DeriveFrom(handling, purpose)</c> of a real strong id, end to end (#2604): inside a handling it
/// derives from the handling and the type's pinned id, the same on a retry; outside it throws. And the gravity guard:
/// every strong id this assembly generates has <c>DeriveFrom</c>, so a generator change that drops it from any id fails
/// here, not in a consumer.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/WhizbangIdGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/StrongIdDerivation.cs</code-under-test>
[Category("ValueObjects")]
public class WhizbangIdDeriveFromTests {
  private static readonly Guid _cause = Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-2c3d4e5f6a7b");

  [Test]
  public async Task DeriveFrom_InAHandling_IsTheDerivationOfThePinnedIdAsync() {
    DeriveFromProbeId id;
    Guid expected;
    using (ReceptorHandling.Enter(_envelope(), "orders-service", "App.OrderReceptor")) {
      id = DeriveFromProbeId.DeriveFrom(Handling.Current, "order");
    }
    using (ReceptorHandling.Enter(_envelope(), "orders-service", "App.OrderReceptor")) {
      expected = StrongIdDerivation.Derive(Handling.Current, DeriveFromProbeId.PinnedId, "order", "AnyName");
    }

    await Assert.That(id.Value).IsEqualTo(expected)
      .Because("the generated id derives from its pinned id, never its name");
    await Assert.That(DeriveFromProbeId.PinnedId).IsEqualTo("5d4c3b2a-1f0e-4d9c-8b7a-6f5e4d3c2b1a");
    await Assert.That(id.GetIsTimeOrdered()).IsTrue();
  }

  [Test]
  public async Task DeriveFrom_ARetryOfTheHandling_DerivesTheSameIdAsync() {
    DeriveFromProbeId first;
    DeriveFromProbeId retry;
    using (ReceptorHandling.Enter(_envelope(), "orders-service", "App.OrderReceptor")) {
      first = DeriveFromProbeId.DeriveFrom(Handling.Current, "order");
    }
    using (ReceptorHandling.Enter(_envelope(), "orders-service", "App.OrderReceptor")) {
      retry = DeriveFromProbeId.DeriveFrom(Handling.Current, "order");
    }

    await Assert.That(retry).IsEqualTo(first);
  }

  [Test]
  public async Task DeriveFrom_OutsideAHandling_ThrowsNamingTheTypeAsync() {
    var thrown = await Assert.That(() => DeriveFromProbeId.DeriveFrom(Handling.Current, "order"))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains("DeriveFromProbeId.DeriveFrom was called outside a receptor handling");
  }

  [Test]
  public async Task DeriveFrom_AnUnpinnedId_ThrowsNamingHowToPinItAsync() {
#pragma warning disable WHIZ118 // the call an unpinned id refuses at compile time, made to show what it does if suppressed
    var thrown = await Assert.That(() => RegistryTestId1.DeriveFrom(Handling.Current, "order")).Throws<NotSupportedException>();
#pragma warning restore WHIZ118

    await Assert.That(thrown!.Message).Contains("RegistryTestId1.DeriveFrom needs a pinned id");
  }

  [Test]
  public async Task EveryGeneratedStrongId_HasDeriveFromAsync() {
    // Reflection in a test only: the guard that the generator gives every strong id DeriveFrom. A generated id is one
    // with the provider the generator always emits beside it; a hand-written IWhizbangId is the author's own.
    var assembly = typeof(WhizbangIdDeriveFromTests).Assembly;
    var ids = assembly.GetTypes()
      .Where(type => type.IsValueType && typeof(IWhizbangId).IsAssignableFrom(type) && assembly.GetType(type.FullName + "Provider") is not null)
      .ToList();
    var missing = ids
      .Where(type => type.GetMethod("DeriveFrom", BindingFlags.Public | BindingFlags.Static, [typeof(Handling), typeof(string)]) is not { } method
        || method.ReturnType != type)
      .Select(type => type.FullName)
      .ToList();

    await Assert.That(ids.Count).IsGreaterThan(3)
      .Because("the guard must look at real generated ids, or it proves nothing");
    await Assert.That(missing).IsEmpty();
  }

  private static MessageEnvelope<DeriveFromProbeMessage> _envelope() => new() {
    MessageId = new MessageId(_cause),
    Payload = new DeriveFromProbeMessage(),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local, HandlerName = "OrderPlacedHandler" },
  };
}

/// <summary>The message the DeriveFrom probes handle.</summary>
public sealed record DeriveFromProbeMessage : IMessage;

/// <summary>A pinned strong id, declared as a struct with <c>[PinnedId]</c>.</summary>
[WhizbangId]
[PinnedId("5D4C3B2A-1F0E-4D9C-8B7A-6F5E4D3C2B1A")]
public readonly partial struct DeriveFromProbeId;
