// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Reconciling a committed redelivery bundle waits for the maintainer's drain (#2001). The bundle's events are stored
/// at receipt, as ordinary event storage; only the reconcile (origin adoption, unhealable marks) is recorded as an
/// integrity delivery, with the same origin, name, event ids and sequences the inline reconcile passes. Without the
/// queue or the drain, it reconciles inline as before.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryReconciliation.cs</code-under-test>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
public class RedeliveryReconcileDeferralTests {
  private const string EVENT_TYPE = "Contracts.OrderPlaced, Contracts";
  private static readonly DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

  private static (RedeliveryComposite Bundle, List<InboxMessage> Children) _bundle(Guid origin, int events) {
    var stream = TrackedGuid.New().Value;
    var bundle = new RedeliveryComposite {
      StreamId = stream,
      OriginServiceId = origin,
      InnerEventIds = [.. Enumerable.Range(0, events).Select(_ => TrackedGuid.New().Value)],
      InnerTypeNames = [.. Enumerable.Range(0, events).Select(_ => EVENT_TYPE)],
      InnerPayloads = [.. Enumerable.Range(0, events).Select(_ => JsonElement.Parse("{}"))],
      InnerCommitSequences = [.. Enumerable.Range(0, events).Select(i => (long?)(i + 1))],
    };
    List<InboxMessage> children = [.. bundle.InnerEventIds.Select((id, i) => new InboxMessage {
      MessageId = id,
      HandlerName = "OrderPlacedHandler",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = new MessageId(id),
        Payload = bundle.InnerPayloads[i],
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Inbox },
      },
      EnvelopeType = "MessageEnvelope",
      StreamId = stream,
      IsEvent = true,
      MessageType = EVENT_TYPE,
      SourceServiceId = origin,
      // The second child arrives without an origin sequence: the origin had not stamped it.
      SourceCommitSequence = i == 1 ? 0 : i + 1,
    })];
    return (bundle, children);
  }

  private static ServiceProvider _services(InMemoryIntegrityDeliveryCoordinator coordinator, bool withDeliveries = true,
      Whizbang.Core.Observability.StreamIntegrityMetrics? metrics = null) {
    var services = new ServiceCollection();
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    if (withDeliveries) {
      services.AddSingleton<IntegrityDeliveryRegistry>();
    }
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task Reconcile_WithTheQueue_IsRecordedForTheMaintainer_NotRunHereAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children) = _bundle(origin, 3);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator();
    await using var services = _services(coordinator);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, NullLogger.Instance, _now, "orders-service");

    await Assert.That(result.Deferred).IsTrue();
    await Assert.That(coordinator.Adoptions).IsEmpty().Because("the reconcile is the maintainer's work now");
    await Assert.That(coordinator.Pending.Count).IsEqualTo(1);
    var delivery = coordinator.Pending[0];
    await Assert.That(delivery.DeliveryType).IsEqualTo(RedeliveryReconciler.DELIVERY_TYPE);
    await Assert.That(delivery.DeliveryId)
      .IsEqualTo(IntegrityDeliveryId.For(RedeliveryReconciler.DELIVERY_TYPE, Encoding.UTF8.GetBytes(delivery.Payload)));
  }

  [Test]
  public async Task TheDrain_ReconcilesWithExactlyWhatTheInlineReconcilePassesAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children) = _bundle(origin, 3);
    var deferring = new InMemoryIntegrityDeliveryCoordinator();
    await using var deferringServices = _services(deferring);
    _ = await RedeliveryReconciler.ReconcileAsync(bundle, children, deferringServices, NullLogger.Instance, _now, "orders-service");
    var inline = new InMemoryIntegrityDeliveryCoordinator { Supported = false };
    await using var inlineServices = _services(inline);
    _ = await RedeliveryReconciler.ReconcileAsync(bundle, children, inlineServices, NullLogger.Instance, _now, "orders-service");

    var registry = deferringServices.GetRequiredService<IntegrityDeliveryRegistry>();
    _ = registry.TryGetHandler(RedeliveryReconciler.DELIVERY_TYPE, out var handler);
    await handler!(deferring.Pending[0].Payload, deferringServices, CancellationToken.None);

    await Assert.That(inline.Adoptions.Count).IsEqualTo(1).Because("a coordinator without the queue reconciles inline, as before");
    await Assert.That(deferring.Adoptions.Count).IsEqualTo(1);
    var drained = deferring.Adoptions[0];
    var direct = inline.Adoptions[0];
    await Assert.That(drained.Origin).IsEqualTo(direct.Origin);
    await Assert.That(drained.Name).IsEqualTo("orders-service");
    await Assert.That(drained.EventIds).IsEquivalentTo(direct.EventIds);
    await Assert.That(drained.Sequences).IsEquivalentTo(new long?[] { 1, null, 3 })
      .Because("a zero sequence is 'not stamped yet' on both paths");
    await Assert.That(direct.Sequences).IsEquivalentTo(drained.Sequences);
  }

  [Test]
  public async Task TheDrain_AnOriginWithoutAName_ReconcilesWithoutOneAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children) = _bundle(origin, 1);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator();
    await using var services = _services(coordinator);
    _ = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, NullLogger.Instance, _now, originServiceName: null);

    _ = services.GetRequiredService<IntegrityDeliveryRegistry>().TryGetHandler(RedeliveryReconciler.DELIVERY_TYPE, out var handler);
    await handler!(coordinator.Pending[0].Payload, services, CancellationToken.None);

    await Assert.That(coordinator.Adoptions.Single().Name).IsNull();
  }

  [Test]
  public async Task TheDrain_ReconcilesWithTheHostsLoggerAndClockAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children) = _bundle(origin, 1);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator();
    var services = new ServiceCollection()
      .AddScoped<IWorkCoordinator>(_ => coordinator)
      .AddSingleton<IntegrityDeliveryRegistry>()
      .AddSingleton<TimeProvider>(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(_now))
      .AddLogging();
    await using var provider = services.BuildServiceProvider();
    _ = await RedeliveryReconciler.ReconcileAsync(bundle, children, provider, NullLogger.Instance, _now, "orders-service");

    await RedeliveryReconciler.HandleDeferredAsync(coordinator.Pending[0].Payload, provider, CancellationToken.None);

    await Assert.That(coordinator.Adoptions.Single().Origin).IsEqualTo(origin);
  }

  [Test]
  public async Task TheDrain_WithoutACoordinator_DoesNothingAsync() {
    await using var services = new ServiceCollection().BuildServiceProvider();
    var payload = """{"originServiceId":"00000000-0000-0000-0000-000000000001","originServiceName":null,"eventIds":[],"commitSequences":[]}""";

    await RedeliveryReconciler.HandleDeferredAsync(payload, services, CancellationToken.None);

    await Assert.That(services.GetService<IWorkCoordinator>()).IsNull();
  }

  [Test]
  public async Task Reconcile_WithoutTheDrain_RunsInlineAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children) = _bundle(origin, 2);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator();
    await using var services = _services(coordinator, withDeliveries: false);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, NullLogger.Instance, _now);

    await Assert.That(result.Deferred).IsFalse();
    await Assert.That(coordinator.Adoptions.Count).IsEqualTo(1);
    await Assert.That(coordinator.Enqueued).IsEmpty();
  }

  [Test]
  public async Task Reconcile_ARedeliveredBundle_IsRecordedOnceAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children) = _bundle(origin, 2);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator();
    using var probe = new IntegrityMetricsProbe();
    await using var services = _services(coordinator, metrics: probe.Metrics);

    _ = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, NullLogger.Instance, _now, "orders-service");
    var again = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, NullLogger.Instance, _now, "orders-service");

    await Assert.That(again.Deferred).IsTrue();
    await Assert.That(coordinator.Pending.Count).IsEqualTo(1);
    await Assert.That(probe.Total(probe.Metrics.IntegrityDeliveriesQueued)).IsEqualTo(1);
  }

  [Test]
  public async Task Reconcile_WhenRecordingFails_LogsTheReconcileFailure_AndLeavesRepairToAskAgainAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children) = _bundle(origin, 2);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator { EnqueueFailure = new InvalidOperationException("db down") };
    await using var services = _services(coordinator);
    var logger = new CapturingLogger();

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, logger, _now);

    await Assert.That(result).IsEqualTo(RedeliveryReconciliation.None);
    await Assert.That(coordinator.Adoptions).IsEmpty();
    await Assert.That(logger.Entries.Single(e => e.Level == LogLevel.Warning).Exception?.Message).IsEqualTo("db down");
  }

  [Test]
  public async Task Reconcile_WhenCanceledWhileRecording_PropagatesAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children) = _bundle(origin, 1);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator { EnqueueFailure = new OperationCanceledException() };
    await using var services = _services(coordinator);

    await Assert.That(async () => await RedeliveryReconciler.ReconcileAsync(bundle, children, services, NullLogger.Instance, _now))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task TheDrain_APayloadThatIsNotAReconcile_IsRefusedAsync() {
    await using var services = new ServiceCollection().BuildServiceProvider();

    await Assert.That(async () => await RedeliveryReconciler.HandleDeferredAsync("[]", services, CancellationToken.None))
      .Throws<JsonException>();
  }

  private sealed class CapturingLogger : ILogger {
    public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, exception));
  }
}
