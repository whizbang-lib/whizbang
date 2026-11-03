// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// An integrity receptor registered through <see cref="IntegrityDeliveryRegistration.RegisterIntegrityReceptor{TMessage}"/>
/// defers at every stage it is registered at (#2001): invoked anywhere other than the maintainer's drain, it records the
/// message in the integrity delivery queue and returns; inside the drain it runs the real receptor. A coordinator
/// without the queue, or a host without the drain, keeps the old inline behavior.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityDeferringReceptor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityDeliveryRegistration.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityDeliveryRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityDeliveryId.cs</code-under-test>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
public class IntegrityDeliveryDeferralTests {
  private static readonly LifecycleStage[] _stages =
    [LifecycleStage.LocalImmediateInline, LifecycleStage.PreOutboxInline, LifecycleStage.PostInboxInline];

  private static RequestRedeliveryCommand _command(string requester = "consumer-svc") => new() {
    RequesterService = requester,
    Topic = "repair-topic",
    EventTypes = ["Contracts.OrderPlaced, Contracts"],
    StreamIds = [new Guid("00000000-0000-0000-0000-000000000042")],
    FromCommitSequence = 10,
    ToCommitSequence = 20,
  };

  private sealed record Fixture(
      ServiceProvider Services, RecordingReceptorRegistry Registry, RecordingReceptor<RequestRedeliveryCommand> Inner,
      InMemoryIntegrityDeliveryCoordinator Coordinator, IntegrityDeliveryRegistry Deliveries) : IAsyncDisposable {
    public IReceptor<RequestRedeliveryCommand> At(LifecycleStage stage) =>
      (IReceptor<RequestRedeliveryCommand>)Registry.Registered.Single(r => r.Stage == stage).Receptor;

    public ValueTask DisposeAsync() => Services.DisposeAsync();
  }

  private static Fixture _build(bool supported = true, bool withDeliveries = true, bool withCoordinator = true,
      Whizbang.Core.Observability.StreamIntegrityMetrics? metrics = null) {
    var coordinator = new InMemoryIntegrityDeliveryCoordinator { Supported = supported };
    var deliveries = new IntegrityDeliveryRegistry();
    var services = new ServiceCollection();
    if (withCoordinator) {
      services.AddScoped<IWorkCoordinator>(_ => coordinator);
    }
    if (withDeliveries) {
      services.AddSingleton(deliveries);
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    var provider = services.BuildServiceProvider();
    var registry = new RecordingReceptorRegistry();
    var inner = new RecordingReceptor<RequestRedeliveryCommand>();
    registry.RegisterIntegrityReceptor(inner, provider, provider.GetRequiredService<IServiceScopeFactory>());
    return new Fixture(provider, registry, inner, coordinator, deliveries);
  }

  [Test]
  public async Task Registration_WrapsTheReceptorAtAllThreeStagesAsync() {
    await using var fx = _build();

    await Assert.That(fx.Registry.Registered.Select(r => r.Stage).ToList()).IsEquivalentTo(_stages.ToList());
    await Assert.That(fx.Registry.Registered.All(r => r.Receptor is IntegrityDeferringReceptor<RequestRedeliveryCommand>))
      .IsTrue().Because("every stage defers, so no path runs integrity work inline on whichever instance received it");
    await Assert.That(fx.Registry.Registered.All(r =>
        ReferenceEquals(((IntegrityDeferringReceptor<RequestRedeliveryCommand>)r.Receptor).Inner, fx.Inner))).IsTrue();
  }

  [Test]
  [Arguments(LifecycleStage.LocalImmediateInline)]
  [Arguments(LifecycleStage.PreOutboxInline)]
  [Arguments(LifecycleStage.PostInboxInline)]
  public async Task OutsideTheDrain_EveryStageRecordsTheDelivery_AndDoesNotRunTheReceptorAsync(LifecycleStage stage) {
    await using var fx = _build();

    await fx.At(stage).HandleAsync(_command());

    await Assert.That(fx.Inner.Handled).IsEmpty().Because("the work waits for the maintainer's drain");
    await Assert.That(fx.Coordinator.Pending.Count).IsEqualTo(1);
    var delivery = fx.Coordinator.Pending[0];
    var wrapper = (IntegrityDeferringReceptor<RequestRedeliveryCommand>)fx.At(stage);
    await Assert.That(delivery.DeliveryType).IsEqualTo(wrapper.DeliveryType);
    await Assert.That(delivery.Payload).Contains("consumer-svc");
    await Assert.That(delivery.DeliveryId)
      .IsEqualTo(IntegrityDeliveryId.For(wrapper.DeliveryType, Encoding.UTF8.GetBytes(delivery.Payload)));
  }

  [Test]
  public async Task OneMessageAtEveryStage_IsRecordedOnceAsync() {
    await using var fx = _build();

    foreach (var stage in _stages) {
      await fx.At(stage).HandleAsync(_command());
    }

    await Assert.That(fx.Coordinator.Enqueued.Count).IsEqualTo(3);
    await Assert.That(fx.Coordinator.Enqueued.Select(e => e.Id).Distinct().Count()).IsEqualTo(1)
      .Because("the delivery id is derived from the type and payload bytes, so every copy of one message is one delivery");
    await Assert.That(fx.Coordinator.Pending.Count).IsEqualTo(1);
  }

  [Test]
  public async Task OnlyANewlyRecordedDelivery_IsCountedAsQueuedAsync() {
    using var probe = new IntegrityMetricsProbe();
    await using var fx = _build(metrics: probe.Metrics);

    foreach (var stage in _stages) {
      await fx.At(stage).HandleAsync(_command());
    }

    await Assert.That(probe.Total(probe.Metrics.IntegrityDeliveriesQueued)).IsEqualTo(1)
      .Because("three copies of one message are one delivery");
  }

  [Test]
  public async Task DifferentMessages_AreDifferentDeliveriesAsync() {
    await using var fx = _build();

    await fx.At(LifecycleStage.PostInboxInline).HandleAsync(_command("a"));
    await fx.At(LifecycleStage.PostInboxInline).HandleAsync(_command("b"));

    await Assert.That(fx.Coordinator.Pending.Count).IsEqualTo(2);
  }

  [Test]
  public async Task ACoordinatorWithoutTheQueue_RunsTheReceptorInline_AsBeforeAsync() {
    await using var fx = _build(supported: false);

    await fx.At(LifecycleStage.PostInboxInline).HandleAsync(_command());

    await Assert.That(fx.Inner.Handled.Count).IsEqualTo(1);
    await Assert.That(fx.Inner.HandledInsideTheDrain[0]).IsFalse();
  }

  [Test]
  public async Task AHostWithoutACoordinator_RunsTheReceptorInlineAsync() {
    await using var fx = _build(withCoordinator: false);

    await fx.At(LifecycleStage.PostInboxInline).HandleAsync(_command());

    await Assert.That(fx.Inner.Handled.Count).IsEqualTo(1);
  }

  [Test]
  public async Task AHostWithoutTheDrain_RunsTheReceptorInline_AndRecordsNothingAsync() {
    await using var fx = _build(withDeliveries: false);

    await fx.At(LifecycleStage.PostInboxInline).HandleAsync(_command());

    await Assert.That(fx.Inner.Handled.Count).IsEqualTo(1)
      .Because("a recorded delivery nothing will ever drain is a lost delivery");
    await Assert.That(fx.Coordinator.Enqueued).IsEmpty();
  }

  [Test]
  public async Task InsideTheDrain_TheWrapperRunsTheReceptorAsync() {
    await using var fx = _build();

    using (IntegrityDrainContext.Enter()) {
      await fx.At(LifecycleStage.LocalImmediateInline).HandleAsync(_command());
    }

    await Assert.That(fx.Inner.Handled.Count).IsEqualTo(1)
      .Because("integrity work the drain itself dispatches is already on the maintainer, under its admission");
    await Assert.That(fx.Coordinator.Enqueued).IsEmpty();
  }

  [Test]
  public async Task TheDrainHandler_ReadsThePayloadBack_AndRunsTheRealReceptorInsideTheDrainAsync() {
    await using var fx = _build();
    await fx.At(LifecycleStage.PostInboxInline).HandleAsync(_command());
    var delivery = fx.Coordinator.Pending[0];

    await Assert.That(fx.Deliveries.TryGetHandler(delivery.DeliveryType, out var handler)).IsTrue();
    using (IntegrityDrainContext.Enter()) {
      await handler!(delivery.Payload, fx.Services, CancellationToken.None);
    }

    await Assert.That(fx.Inner.Handled.Count).IsEqualTo(1);
    var handled = fx.Inner.Handled[0];
    await Assert.That(handled.RequesterService).IsEqualTo("consumer-svc");
    await Assert.That(handled.Topic).IsEqualTo("repair-topic");
    await Assert.That(handled.FromCommitSequence).IsEqualTo(10);
    await Assert.That(handled.ToCommitSequence).IsEqualTo(20);
    await Assert.That(handled.StreamIds!.Single()).IsEqualTo(new Guid("00000000-0000-0000-0000-000000000042"));
    await Assert.That(fx.Inner.HandledInsideTheDrain[0]).IsTrue();
  }

  [Test]
  public async Task TheDrainContext_EndsWithItsScope_AndNestsAsync() {
    await Assert.That(IntegrityDrainContext.IsActive).IsFalse();
    using (IntegrityDrainContext.Enter()) {
      using (IntegrityDrainContext.Enter()) {
        await Assert.That(IntegrityDrainContext.IsActive).IsTrue();
      }
      await Assert.That(IntegrityDrainContext.IsActive).IsTrue().Because("leaving an inner scope restores the outer one");
    }
    await Assert.That(IntegrityDrainContext.IsActive).IsFalse();
  }

  [Test]
  public async Task ANullMessage_IsRefusedAsync() {
    await using var fx = _build();

    await Assert.That(async () => await fx.At(LifecycleStage.PostInboxInline).HandleAsync(null!))
      .Throws<ArgumentNullException>();
  }
}

/// <summary>The registry the drain finds handlers in, and the content-derived delivery identity.</summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityDeliveryRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityDeliveryId.cs</code-under-test>
public class IntegrityDeliveryRegistryTests {
  [Test]
  public async Task AnUnknownType_HasNoHandlerAsync() {
    var registry = new IntegrityDeliveryRegistry();

    await Assert.That(registry.TryGetHandler("nobody-registered-this", out _)).IsFalse();
  }

  [Test]
  public async Task TheFirstRegistrationOfAType_WinsAsync() {
    var registry = new IntegrityDeliveryRegistry();
    var calls = new List<string>();
    registry.Register("t", (_, _, _) => { calls.Add("first"); return Task.CompletedTask; });
    registry.Register("t", (_, _, _) => { calls.Add("second"); return Task.CompletedTask; });

    _ = registry.TryGetHandler("t", out var handler);
    await handler!("{}", new ServiceCollection().BuildServiceProvider(), CancellationToken.None);

    await Assert.That(calls).IsEquivalentTo(new List<string> { "first" })
      .Because("registrars may start more than once; a host that registers its own handler first keeps it");
  }

  [Test]
  public async Task TheReconcileHandler_IsRegisteredByTheRegistryItselfAsync() {
    var registry = new IntegrityDeliveryRegistry();

    await Assert.That(registry.TryGetHandler(RedeliveryReconciler.DELIVERY_TYPE, out _)).IsTrue()
      .Because("the reconcile is Core's own deferred work; no registrar has to remember to add it");
  }

  [Test]
  public async Task TheDeliveryId_IsAFunctionOfTypeAndPayloadAsync() {
    var payload = Encoding.UTF8.GetBytes("""{"a":1}""");

    var id = IntegrityDeliveryId.For("t", payload);

    await Assert.That(IntegrityDeliveryId.For("t", Encoding.UTF8.GetBytes("""{"a":1}"""))).IsEqualTo(id);
    await Assert.That(IntegrityDeliveryId.For("t", Encoding.UTF8.GetBytes("""{"a":2}"""))).IsNotEqualTo(id);
    await Assert.That(IntegrityDeliveryId.For("u", payload)).IsNotEqualTo(id);
    // "t" + "a" payload must not collide with "ta" + "" : the type is length-delimited from the payload.
    await Assert.That(IntegrityDeliveryId.For("ta", []))
      .IsNotEqualTo(IntegrityDeliveryId.For("t", Encoding.UTF8.GetBytes("a")));
    await Assert.That(id.Version).IsEqualTo(8).Because("an RFC 9562 version 8 (custom, name-derived) identifier");
  }

  [Test]
  public async Task ANullType_IsRefusedAsync() {
    await Assert.That(() => IntegrityDeliveryId.For(null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => new IntegrityDeliveryRegistry().Register(null!, (_, _, _) => Task.CompletedTask))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new IntegrityDeliveryRegistry().Register("t", null!)).Throws<ArgumentNullException>();
  }
}

/// <summary>A recorded delivery that reads back as null is a fault the drain counts, never a silent success.</summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityDeferringReceptor.cs</code-under-test>
public class IntegrityDeferringReceptorPayloadTests {
  [Test]
  public async Task APayloadThatReadsBackAsNull_IsAFaultAsync() {
    var deliveries = new IntegrityDeliveryRegistry();
    await using var provider = new ServiceCollection().AddSingleton(deliveries).BuildServiceProvider();
    var registry = new RecordingReceptorRegistry();
    var inner = new RecordingReceptor<RequestRedeliveryCommand>();
    registry.RegisterIntegrityReceptor(inner, provider, provider.GetRequiredService<IServiceScopeFactory>());
    var type = ((IntegrityDeferringReceptor<RequestRedeliveryCommand>)registry.Registered[0].Receptor).DeliveryType;
    _ = deliveries.TryGetHandler(type, out var handler);

    await Assert.That(async () => await handler!("null", provider, CancellationToken.None))
      .Throws<System.Text.Json.JsonException>();
    await Assert.That(inner.Handled).IsEmpty();
  }
}
