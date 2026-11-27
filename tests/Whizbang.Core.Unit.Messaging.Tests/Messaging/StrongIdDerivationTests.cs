// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// A strong id derives from the handling it is created in (#2604): the handled message, the service, the handler, the
/// receptor, the id type's pinned id and the purpose. A retry of the handling derives the same id; outside a handling,
/// or after it ended, deriving throws; one id per purpose per handling; the type's name takes no part, so a rename keeps
/// the ids it derives.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/StrongIdDerivation.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/Handling.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorHandling.cs</code-under-test>
[Category("Messaging")]
public class StrongIdDerivationTests {
  private const string PIN = "3f2c8a1e-5b7d-4c9e-a1f0-6d2b8e4c7a91";
  private const string OTHER_PIN = "7b1d9e2f-0c3a-4e5b-8f6d-1a2b3c4d5e6f";
  private const string SERVICE = "orders-service";
  private const string HANDLER = "OrderPlacedHandler";
  private const string RECEPTOR = "App.OrderReceptor";
  private static readonly Guid _cause = Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-2c3d4e5f6a7b");

  [Test]
  public async Task Derive_FixedInputs_DeriveTheCompatibilityValueAsync() {
    // The value is a compatibility contract: a stored strong id is referenced by every record that names it, so a
    // layout change would make a retry across the deploy derive a different id. Computed by an independent reference
    // of the layout, not by this implementation.
    var id = _deriveIn(_envelope(_cause), SERVICE, RECEPTOR, PIN, "order");

    await Assert.That(id).IsEqualTo(Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-1123cd895f8b"));
  }

  [Test]
  public async Task Derive_AHandlingNamingNoServiceHandlerOrReceptor_DerivesTheCompatibilityValueAsync() {
    var envelope = _envelope(_cause, handlerName: null);

    Guid id;
    using (ReceptorHandling.Enter(envelope)) {
      id = StrongIdDerivation.Derive(Handling.Current, PIN, "order", "OrderId");
    }

    await Assert.That(id).IsEqualTo(Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-b64e17db5302"));
  }

  [Test]
  public async Task Derive_ARetryOfTheHandling_DerivesTheSameIdAsync() {
    // A retry materializes a fresh envelope for the same message and enters a fresh handling
    var first = _deriveIn(_envelope(_cause), SERVICE, RECEPTOR, PIN, "order");
    var retry = _deriveIn(_envelope(_cause), SERVICE, RECEPTOR, PIN, "order");

    await Assert.That(retry).IsEqualTo(first);
  }

  [Test]
  public async Task Derive_TheIdTypesName_TakesNoPart_SoARenameKeepsTheIdAsync() {
    Guid before;
    Guid after;
    using (ReceptorHandling.Enter(_envelope(_cause), SERVICE, RECEPTOR)) {
      before = StrongIdDerivation.Derive(Handling.Current, PIN, "order", "OrderId");
    }
    using (ReceptorHandling.Enter(_envelope(_cause), SERVICE, RECEPTOR)) {
      after = StrongIdDerivation.Derive(Handling.Current, PIN, "order", "PurchaseId");
    }

    await Assert.That(after).IsEqualTo(before)
      .Because("the pinned id is the derivation input, never the type's name");
  }

  [Test]
  public async Task Derive_ThePinnedIdsSpelling_TakesNoPartAsync() {
    var lower = _deriveIn(_envelope(_cause), SERVICE, RECEPTOR, PIN, "order");
    var upperNoHyphens = _deriveIn(_envelope(_cause), SERVICE, RECEPTOR, Guid.Parse(PIN).ToString("N").ToUpperInvariant(), "order");

    await Assert.That(upperNoHyphens).IsEqualTo(lower);
  }

  [Test]
  public async Task Derive_EachInput_SeparatesTheIdsAsync() {
    var baseline = _deriveIn(_envelope(_cause), SERVICE, RECEPTOR, PIN, "order");
    Guid[] variants = [
      _deriveIn(_envelope(Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-2c3d4e5f6a7c")), SERVICE, RECEPTOR, PIN, "order"),
      _deriveIn(_envelope(_cause), "billing-service", RECEPTOR, PIN, "order"),
      _deriveIn(_envelope(_cause, handlerName: "OtherHandler"), SERVICE, RECEPTOR, PIN, "order"),
      _deriveIn(_envelope(_cause), SERVICE, "App.OtherReceptor", PIN, "order"),
      _deriveIn(_envelope(_cause), SERVICE, RECEPTOR, OTHER_PIN, "order"),
      _deriveIn(_envelope(_cause), SERVICE, RECEPTOR, PIN, "invoice"),
    ];

    foreach (var variant in variants) {
      await Assert.That(variant).IsNotEqualTo(baseline);
    }
    await Assert.That(variants.Distinct().Count()).IsEqualTo(variants.Length);
  }

  [Test]
  public async Task Derive_SortsAtTheHandledMessage_AndIsAVersion7IdAsync() {
    var id = _deriveIn(_envelope(_cause), SERVICE, RECEPTOR, PIN, "order");
    var bytes = id.ToByteArray(bigEndian: true);
    var cause = _cause.ToByteArray(bigEndian: true);

    await Assert.That(bytes[..10]).IsEquivalentTo(cause[..10])
      .Because("a derived id keeps the cause's millisecond and counter, so it sorts with the handling (UUIDv7, #2379)");
    await Assert.That(id.Version).IsEqualTo(7);
    await Assert.That(bytes[8] & 0xC0).IsEqualTo(0x80);
  }

  [Test]
  public async Task Derive_ANestedCascadeHandling_AnchorsOnTheCascadedMessageAsync() {
    var cascaded = Guid.Parse("0190a5b2-3c4d-7e5f-8a1b-aaaaaaaaaaaa");
    var wrapper = new CascadeEnvelopeWrapper(_envelope(_cause)) { EmissionAnchor = cascaded };

    var nested = _deriveIn(wrapper, SERVICE, RECEPTOR, PIN, "order");
    var direct = _deriveIn(_envelope(cascaded, handlerName: wrapper.DispatchContext.HandlerName), SERVICE, RECEPTOR, PIN, "order");

    await Assert.That(nested).IsEqualTo(direct)
      .Because("a receptor of a cascaded message derives from that message, as its emissions do");
  }

  [Test]
  public async Task Derive_OutsideAHandling_ThrowsNamingWhyAsync() {
    var handling = Handling.Current;

    await Assert.That(handling.IsActive).IsFalse();
    var thrown = await Assert.That(() => StrongIdDerivation.Derive(handling, PIN, "order", "OrderId"))
      .Throws<InvalidOperationException>();
    await Assert.That(thrown!.Message).Contains("OrderId.DeriveFrom");
    await Assert.That(thrown.Message).Contains("outside a receptor handling");
    await Assert.That(thrown.Message).Contains("OrderId.New()");
  }

  [Test]
  public async Task Derive_AfterTheHandlingEnded_ThrowsNamingWhyAsync() {
    Handling captured;
    using (ReceptorHandling.Enter(_envelope(_cause), SERVICE, RECEPTOR)) {
      captured = Handling.Current;
      await Assert.That(captured.IsActive).IsTrue();
    }

    await Assert.That(captured.IsActive).IsFalse();
    var thrown = await Assert.That(() => StrongIdDerivation.Derive(captured, PIN, "order", "OrderId"))
      .Throws<InvalidOperationException>();
    await Assert.That(thrown!.Message).Contains("has ended");
  }

  [Test]
  public async Task Derive_TheSamePurposeTwiceInOneHandling_ThrowsAsync() {
    using (ReceptorHandling.Enter(_envelope(_cause), SERVICE, RECEPTOR)) {
      StrongIdDerivation.Derive(Handling.Current, PIN, "line", "LineId");

      var thrown = await Assert.That(() => StrongIdDerivation.Derive(Handling.Current, PIN, "line", "LineId"))
        .Throws<InvalidOperationException>();
      await Assert.That(thrown!.Message).Contains("'line'");
      await Assert.That(thrown.Message).Contains("one id per purpose");
    }
  }

  [Test]
  public async Task Derive_TheSamePurposeForTwoIdTypes_DerivesBothAsync() {
    using (ReceptorHandling.Enter(_envelope(_cause), SERVICE, RECEPTOR)) {
      var order = StrongIdDerivation.Derive(Handling.Current, PIN, "main", "OrderId");
      var invoice = StrongIdDerivation.Derive(Handling.Current, OTHER_PIN, "main", "InvoiceId");

      await Assert.That(invoice).IsNotEqualTo(order);
    }
  }

  [Test]
  public async Task Derive_AHandledMessageWithNoId_ThrowsAsync() {
    using (ReceptorHandling.Enter(_envelope(Guid.Empty), SERVICE, RECEPTOR)) {
      var thrown = await Assert.That(() => StrongIdDerivation.Derive(Handling.Current, PIN, "order", "OrderId"))
        .Throws<InvalidOperationException>();
      await Assert.That(thrown!.Message).Contains("no id");
    }
  }

  [Test]
  public async Task Derive_InvalidArguments_AreRefusedAsync() {
    using (ReceptorHandling.Enter(_envelope(_cause), SERVICE, RECEPTOR)) {
      await Assert.That(() => StrongIdDerivation.Derive(null!, PIN, "order", "OrderId")).Throws<ArgumentNullException>();
      await Assert.That(() => StrongIdDerivation.Derive(Handling.Current, PIN, "order", null!)).Throws<ArgumentNullException>();
      await Assert.That(() => StrongIdDerivation.Derive(Handling.Current, PIN, null!, "OrderId")).Throws<ArgumentNullException>();
      await Assert.That(() => StrongIdDerivation.Derive(Handling.Current, PIN, " ", "OrderId")).Throws<ArgumentException>();
      await Assert.That(() => StrongIdDerivation.Derive(Handling.Current, null!, "order", "OrderId")).Throws<ArgumentNullException>();
      await Assert.That(() => StrongIdDerivation.Derive(Handling.Current, "not-a-guid", "order", "OrderId")).Throws<ArgumentException>();
    }
  }

  [Test]
  public async Task NotPinned_NamesTheTypeAndHowToPinItAsync() {
    var exception = StrongIdDerivation.NotPinned("OrderId");

    await Assert.That(exception.Message).Contains("OrderId");
    await Assert.That(exception.Message).Contains("[PinnedId");
    await Assert.That(exception.Message).Contains("PinnedId =");
  }

  [Test]
  public async Task NotPinned_ANullTypeName_IsRefusedAsync() {
    await Assert.That(() => StrongIdDerivation.NotPinned(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  [NotInParallel("StrongIdDerivationProbe")] // the probe message's receptor registrations: a test beside it registering the same message type would see these handlings
  public async Task InvokeAsync_TwoReceptorsOfOneMessage_DeriveTwoIdsForOnePurposeAsync() {
    // Owner decision on #2274: two receptors in one service handling the same message take two ids (handler identity in
    // the hash), as their emissions do. The invoker names the service and the receptor in each body's handling.
    var derived = new List<Guid>();
    var registry = new Whizbang.Core.Unit.Messaging.Tests.Generated.GeneratedReceptorRegistry();
    registry.Register<StrongIdDerivationProbeMessage>(
      new StrongIdDerivationFirstReceptor(() => derived.Add(StrongIdDerivation.Derive(Handling.Current, PIN, "order", "OrderId"))),
      LifecycleStage.PostInboxInline);
    registry.Register<StrongIdDerivationProbeMessage>(
      new StrongIdDerivationSecondReceptor(() => derived.Add(StrongIdDerivation.Derive(Handling.Current, PIN, "order", "OrderId"))),
      LifecycleStage.PostInboxInline);
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    await using var provider = services.BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);
    var envelope = new MessageEnvelope<StrongIdDerivationProbeMessage> {
      MessageId = new MessageId(_cause),
      Payload = new StrongIdDerivationProbeMessage(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local, HandlerName = HANDLER },
    };

    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    await Assert.That(derived).Count().IsEqualTo(2);
    await Assert.That(derived[1]).IsNotEqualTo(derived[0]);
    await Assert.That(Handling.Current.IsActive).IsFalse()
      .Because("after the bodies return, the caller is no longer in a handling");
  }

  private static Guid _deriveIn(IMessageEnvelope envelope, string service, string receptor, string pin, string purpose) {
    using (ReceptorHandling.Enter(envelope, service, receptor)) {
      return StrongIdDerivation.Derive(Handling.Current, pin, purpose, "OrderId");
    }
  }

  private static MessageEnvelope<StrongIdDerivationProbeMessage> _envelope(Guid messageId, string? handlerName = HANDLER) => new() {
    MessageId = new MessageId(messageId),
    Payload = new StrongIdDerivationProbeMessage(),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local, HandlerName = handlerName },
  };
}

/// <summary>The message the strong id derivation probes handle.</summary>
public sealed record StrongIdDerivationProbeMessage : IMessage;

/// <summary>One of two receptors of the probe message. Registered only at runtime by the tests above.</summary>
[SuppressReceptorRegistration]
public sealed class StrongIdDerivationFirstReceptor(Action onReceived) : IReceptor<StrongIdDerivationProbeMessage> {
  /// <inheritdoc />
  public ValueTask HandleAsync(StrongIdDerivationProbeMessage message, CancellationToken cancellationToken = default) {
    onReceived();
    return ValueTask.CompletedTask;
  }
}

/// <summary>The other receptor of the probe message. Registered only at runtime by the tests above.</summary>
[SuppressReceptorRegistration]
public sealed class StrongIdDerivationSecondReceptor(Action onReceived) : IReceptor<StrongIdDerivationProbeMessage> {
  /// <inheritdoc />
  public ValueTask HandleAsync(StrongIdDerivationProbeMessage message, CancellationToken cancellationToken = default) {
    onReceived();
    return ValueTask.CompletedTask;
  }
}
