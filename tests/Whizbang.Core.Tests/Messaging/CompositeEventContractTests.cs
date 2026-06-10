#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the <see cref="ICompositeEvent"/> contract — the marker interface
/// W3 slices 9–11 (producer ergonomics, receiver expansion, batched
/// event-store append) all build on. The contract itself has no behavior;
/// these tests pin the shape so the composite-events feature can ship
/// incrementally without churn.
/// </summary>
/// <docs>fundamentals/messaging/composite-events</docs>
public class CompositeEventContractTests {

  [Test]
  public async Task ICompositeEvent_ExtendsIMessage_SoExistingPipelinesCanCarryItAsync() {
    // Composites flow through the same dispatcher / outbox / transport
    // surface as ordinary events — that only works because the interface
    // root is IMessage. A composite that did NOT extend IMessage would
    // need a parallel dispatch surface.
    ICompositeEvent composite = new _bulkJobImportComposite([]);

    await Assert.That(composite is IMessage).IsTrue()
      .Because("ICompositeEvent : IMessage — the dispatcher / outbox / transport ALL constrain on IMessage. The composite-events feature reuses the existing pipeline; it does not introduce a parallel one.");
  }

  [Test]
  public async Task ICompositeEvent_MaxInnerEventsAllowed_DefaultIs10KAsync() {
    // The default cap is surfaced on the interface so producers
    // intentionally raising it MUST override; accidental composites
    // that yield millions of inner events get caught at the cap rather
    // than corrupting downstream batched appends.
    ICompositeEvent composite = new _bulkJobImportComposite([]);

    await Assert.That(composite.MaxInnerEventsAllowed).IsEqualTo(10_000)
      .Because("Default cap of 10K matches the W3 plan's '5000-job bulk import' upper end with 2x headroom — producers raising past 10K should override the property and document why.");
  }

  [Test]
  public async Task ICompositeEvent_MaxInnerEventsAllowed_OverridableAsync() {
    // Producers with a defensible reason to allow more (e.g., a known
    // 50K-row import pattern) can override the cap on their composite
    // type — proving the property is virtual / default-impl-overridable.
    ICompositeEvent composite = new _largeBulkComposite([]);

    await Assert.That(composite.MaxInnerEventsAllowed).IsEqualTo(50_000)
      .Because("Per-composite overrides MUST be honored so producers with vetted high-volume patterns aren't forced into the default cap.");
  }

  [Test]
  public async Task ICompositeEvent_InnerEvents_YieldsInProducerOrderAsync() {
    // Receiver enumerates exactly once and processes in yielded order.
    // The order is part of the contract: per resolved design (W3 notes
    // 2026-06-09), inner events are sequential within a composite.
    var inner = new IMessage[] {
      new _jobCreatedEvent("J-001"),
      new _jobCreatedEvent("J-002"),
      new _jobCreatedEvent("J-003"),
    };
    ICompositeEvent composite = new _bulkJobImportComposite(inner);

    var collected = composite.InnerEvents.ToList();

    await Assert.That(collected.Count).IsEqualTo(3);
    await Assert.That(((_jobCreatedEvent)collected[0]).JobId).IsEqualTo("J-001");
    await Assert.That(((_jobCreatedEvent)collected[1]).JobId).IsEqualTo("J-002");
    await Assert.That(((_jobCreatedEvent)collected[2]).JobId).IsEqualTo("J-003");
  }

  [Test]
  public async Task ICompositeEvent_InnerEvents_LazyEnumerationSupportedAsync() {
    // Producers MAY allocate inner events lazily. The contract is "yields
    // in order" — not "must materialize an array first." A producer that
    // builds inner events from a streaming source (DB cursor, large IO)
    // should be able to use yield return.
    var compositeWithLazyYield = new _lazyComposite(count: 5);

    var enumerated = compositeWithLazyYield.InnerEvents.ToList();

    await Assert.That(enumerated.Count).IsEqualTo(5);
    await Assert.That(compositeWithLazyYield.MaterializeCalls).IsEqualTo(0)
      .Because("Lazy enumeration MUST be possible — producers building inner events from streaming sources (DB cursors, large IO) shouldn't be forced to materialize them upfront.");
  }

  // ============================================================
  // Test composites
  // ============================================================

  /// <summary>Bulk job import composite — represents a JDX bulk-import operation that emits N JobCreatedEvents.</summary>
  private sealed class _bulkJobImportComposite(IReadOnlyList<IMessage> inner) : ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => inner;
  }

  /// <summary>Composite that opts into a 50K cap — proves overrides work.</summary>
  private sealed class _largeBulkComposite(IReadOnlyList<IMessage> inner) : ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => inner;
    public int MaxInnerEventsAllowed => 50_000;
  }

  /// <summary>Composite that yield-returns inner events lazily without materializing them upfront.</summary>
  private sealed class _lazyComposite : ICompositeEvent {
    public _lazyComposite(int count) {
      _count = count;
    }
    private readonly int _count;
    public int MaterializeCalls { get; private set; }
    public IEnumerable<IMessage> InnerEvents {
      get {
        for (var i = 0; i < _count; i++) {
          yield return new _jobCreatedEvent($"L-{i:D3}");
        }
      }
    }
  }

  private sealed record _jobCreatedEvent(string JobId) : IEvent;
}
