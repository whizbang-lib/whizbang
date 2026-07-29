#pragma warning disable CA1707

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the dispatch-time fan-out contract (<see cref="CompositeInboxFanout"/>): a composite event
/// arriving as an inbox row expands into N child inbox messages, each carrying the inner event,
/// inheriting the composite's identity context, with a fresh MessageId. Cap / expansion failures are
/// returned (not thrown) so the dispatch worker can dead-letter the composite row. The real JSON
/// serialization is covered by EnvelopeSerializerTests + JsonContextRegistryTests; here a fake
/// serializer isolates the fan-out orchestration logic.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#dispatch-fanout</docs>
[Category("Messaging")]
public class CompositeInboxFanoutTests {

  [Test]
  public async Task TryExpand_NonComposite_ReturnsNotCompositeAsync() {
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite: null, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.NotComposite);
    await Assert.That(result.Children).IsEmpty();
  }

  [Test]
  public async Task TryExpand_YieldsOneChildInboxMessagePerInnerAsync() {
    var streamId = Guid.NewGuid();
    var composite = new _testComposite(new _innerEvent("J-001"), new _innerEvent("J-002"), new _innerEvent("J-003"));
    var source = _sourceEnvelope(streamId);
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(3);
    // Each child's MessageType is the concrete inner event's assembly-qualified name.
    await Assert.That(result.Children.All(c => c.MessageType.Contains("_innerEvent", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task TryExpand_ChildrenInheritCompositeStreamIdFromHopsAsync() {
    var streamId = Guid.NewGuid();
    var composite = new _testComposite(new _innerEvent("X"));
    var source = _sourceEnvelope(streamId);
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    var child = result.Children.Single();
    await Assert.That(child.StreamId).IsEqualTo(streamId)
      .Because("Inner events inherit the composite's stream — the first hop's AggregateId is the composite StreamId.");
  }

  [Test]
  public async Task TryExpand_AssignsFreshDistinctMessageIdsPerChildAsync() {
    var composite = new _testComposite(new _innerEvent("A"), new _innerEvent("B"));
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Children[0].MessageId).IsNotEqualTo(result.Children[1].MessageId);
    await Assert.That(result.Children[0].MessageId).IsNotEqualTo(source.MessageId.Value)
      .Because("Children must not collide with the composite's MessageId or each other — inbox dedup keeps them distinct.");
  }

  [Test]
  public async Task TryExpand_ChildrenAreMarkedAsEventsAsync() {
    var composite = new _testComposite(new _innerEvent("E"));
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Children.Single().IsEvent).IsTrue()
      .Because("The inner events implement IEvent, so the child inbox rows persist to the event store.");
  }

  [Test]
  public async Task TryExpand_ChildrenCarryCompositeLineage_CausationIsCompositeMessageIdAsync() {
    // Each child's creation hop must point back to the parent composite so "these events came from
    // composite X" is queryable off the event-store rows (Hops[0].CausationId / CausationType).
    var composite = new _testComposite(new _innerEvent("J-1"), new _innerEvent("J-2"));
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Children.Count).IsEqualTo(2);
    foreach (var child in result.Children) {
      var hop0 = child.Metadata!.Hops[0];
      await Assert.That(hop0.CausationId).IsEqualTo(source.MessageId)
        .Because("the child's creation hop is caused by the composite — CausationId is the composite's MessageId.");
      await Assert.That(hop0.CausationType).IsEqualTo(nameof(_testComposite))
        .Because("CausationType records that the cause was this composite type.");
    }
    // All children of one composite share the same causation → groupable as one batch.
    await Assert.That(result.Children[0].Metadata!.Hops[0].CausationId)
      .IsEqualTo(result.Children[1].Metadata!.Hops[0].CausationId);
  }

  [Test]
  public async Task TryExpand_ChildrenCarryNoRebroadcastFlagAsync() {
    var composite = new _testComposite(new _innerEvent("J-1"), new _innerEvent("J-2"));
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Children.All(c => (c.Flags & EventFlags.NoRebroadcast) != 0)).IsTrue()
      .Because("Every fan-out child is stamped NoRebroadcast so the outbox-enqueue boundary can drop any re-broadcast.");
  }

  [Test]
  public async Task TryExpand_CollectiveInnerEvent_ChildKeepsCollectiveFlagAsync() {
    // A collective event carried INSIDE a composite must behave on the receiving service exactly as
    // a locally-emitted collective event would: its child inbox row needs EventFlags.Collective or
    // the inbox emit chain never routes it to the collective sink. NoRebroadcast must still be
    // present — deriving the inner event's real flags never replaces the fan-out containment marker.
    var composite = new _testComposite(new _collectiveInnerEvent(new TenantCollectiveScope("tenant-1"), []));
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Children.Count).IsEqualTo(1);
    await Assert.That(result.Children[0].Flags & EventFlags.Collective).IsEqualTo(EventFlags.Collective)
      .Because("the child of a composite keeps the inner event's own marker flags — a collective " +
               "inner event that loses Collective is silently never applied on the receiving service.");
    await Assert.That(result.Children[0].Flags & EventFlags.NoRebroadcast).IsEqualTo(EventFlags.NoRebroadcast);
  }

  [Test]
  public async Task TryExpand_OverCap_ReturnsCapExceededAsync() {
    var inners = Enumerable.Range(0, 11).Select(i => new _innerEvent($"i-{i}")).ToArray();
    var composite = new _testComposite(inners) { MaxInnerEventsAllowedOverride = 10 };
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.CapExceeded);
    await Assert.That(result.Children).IsEmpty()
      .Because("No partial fan-out — a cap breach dead-letters the whole composite.");
    await Assert.That(result.CompositeTypeName).IsNotNull();
  }

  [Test]
  public async Task TryExpand_NullInner_Atomic_ReturnsFailedAsync() {
    var composite = new _nullYieldingComposite { AtomicityOverride = FanoutAtomicity.Atomic };
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Failed)
      .Because("Atomic: any bad child sinks the whole composite.");
    await Assert.That(result.Children).IsEmpty();
  }

  [Test]
  public async Task TryExpand_NullInner_Independent_DropsBadChildAndKeepsRestAsync() {
    // Independent (default): a null inner is dropped; the valid inner still fans out.
    var composite = new _mixedNullComposite();
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(1)
      .Because("Independent: one bad child doesn't sink the batch — the good child survives.");
  }

  [Test]
  public async Task TryExpand_NullInner_Independent_LogsTheDroppedChildAsync() {
    // Independent mode drops a bad child, but the drop must be LOGGED — a partial fan-out that
    // silently reports Expanded is invisible message loss (the swallow-audit finding).
    var captured = new _capturingLogger();
    var composite = new _mixedNullComposite();
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _providerWithLogger(captured);

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(1);
    await Assert.That(captured.Entries.Any(e => e.Level == LogLevel.Warning)).IsTrue()
      .Because("a dropped inner event must be logged, not silently swallowed");
  }

  [Test]
  public async Task TryExpand_ReplacementInner_FansOutTheReplacementSetAsync() {
    // A pre-fanout ReplaceWith directive supplies the children to fan out instead of InnerEvents.
    var composite = new _testComposite(new _innerEvent("original"));
    var source = _sourceEnvelope(Guid.NewGuid());
    var sp = _provider();
    var replacement = new IMessage[] { new _innerEvent("R-1"), new _innerEvent("R-2") };

    var result = CompositeInboxFanout.TryExpand(composite, source, sp, replacement);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(2)
      .Because("The replacement set (2) is fanned out, not the composite's own InnerEvents (1).");
  }

  // ============================================================
  // Fakes + helpers
  // ============================================================

  private static ServiceProvider _provider() =>
    new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new _fakeSerializer())
      .BuildServiceProvider();

  private static ServiceProvider _providerWithLogger(_capturingLogger captured) =>
    new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new _fakeSerializer())
      .AddLogging(b => b.AddProvider(new _capturingLoggerProvider(captured)))
      .BuildServiceProvider();

  /// <summary>Captures log entries emitted during fan-out for assertions.</summary>
  private sealed class _capturingLogger {
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
  }

  private sealed class _capturingLoggerProvider(_capturingLogger captured) : ILoggerProvider {
    public ILogger CreateLogger(string categoryName) => new _sink(captured);
    public void Dispose() { }

    private sealed class _sink(_capturingLogger captured) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => captured.Entries.Add((logLevel, formatter(state, exception), exception));
    }
  }

  /// <summary>
  /// A source inbox envelope whose first hop carries the composite's StreamId as AggregateId — the
  /// shape <c>_extractStreamId</c> reads to inherit the stream onto each child.
  /// </summary>
  private static MessageEnvelope<JsonElement> _sourceEnvelope(Guid streamId) {
    var aggregateMeta = new Dictionary<string, JsonElement> {
      ["AggregateId"] = JsonSerializer.SerializeToElement(streamId.ToString()),
    };
    return new MessageEnvelope<JsonElement> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Metadata = aggregateMeta,
      }],
      SourceServiceId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
      SourceCommitSequence = 42,
    };
  }

  /// <summary>
  /// Minimal serializer: records the payload's runtime AQN as MessageType and produces a JsonElement
  /// envelope. The real serializer is tested elsewhere — this isolates fan-out orchestration.
  /// </summary>
  private sealed class _fakeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var payloadType = envelope.Payload!.GetType();
      var aqn = payloadType.AssemblyQualifiedName!;
      var jsonEnv = new MessageEnvelope<JsonElement> {
        DispatchContext = envelope.DispatchContext,
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = envelope.Hops?.ToList() ?? [],
      };
      return new SerializedEnvelope(
        JsonEnvelope: jsonEnv,
        EnvelopeType: $"Whizbang.Core.Observability.MessageEnvelope`1[[{aqn}]], Whizbang.Core",
        MessageType: aqn);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed record _innerEvent(string Id) : IEvent;
  private sealed record _collectiveInnerEvent(CollectiveScope Scope, IReadOnlyList<Guid> MatchedStreamIds) : ICollectiveEvent;

  private sealed class _nullYieldingComposite : ICompositeEvent {
    public int MaxInnerEventsAllowed => 10;
    public FanoutAtomicity AtomicityOverride { get; init; } = FanoutAtomicity.Independent;
    public FanoutAtomicity Atomicity => AtomicityOverride;
    public IEnumerable<IMessage> InnerEvents {
      get {
        yield return null!;
      }
    }
  }

  private sealed class _mixedNullComposite : ICompositeEvent {
    public int MaxInnerEventsAllowed => 10;
    // Default Atomicity (Independent) via the interface default-impl.
    public IEnumerable<IMessage> InnerEvents {
      get {
        yield return null!;
        yield return new _innerEvent("good");
      }
    }
  }

  private sealed class _testComposite : ICompositeEvent {
    public _testComposite(params IEvent[] inner) {
      _inner = inner;
    }
    private readonly IEvent[] _inner;
    public int? MaxInnerEventsAllowedOverride { get; init; }
    public int MaxInnerEventsAllowed => MaxInnerEventsAllowedOverride ?? 10_000;
    public IEnumerable<IMessage> InnerEvents => _inner;
  }
}
