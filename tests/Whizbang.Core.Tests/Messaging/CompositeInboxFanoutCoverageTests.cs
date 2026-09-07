using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Covers the private <c>CompositeInboxFanout._extractStreamId</c> fallback branch — every scenario
/// in the sibling <c>CompositeInboxFanoutTests</c> builds its source envelope via a helper that
/// always stamps the first hop's <c>AggregateId</c> metadata, so the "no AggregateId present"
/// fallback (child inherits the composite's own MessageId as its stream) is never reached there.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/CompositeInboxFanout.cs</code-under-test>
[Category("Messaging")]
public class CompositeInboxFanoutCoverageTests {

  [Test]
  public async Task TryExpand_SourceHopHasNoAggregateIdMetadata_ChildStreamIdFallsBackToSourceMessageIdAsync() {
    // Mirrors the transport-edge fallback: a composite whose first hop carries no AggregateId
    // (metadata is null) must still land its children on a deterministic, non-empty stream —
    // the composite's own MessageId — rather than Guid.Empty, which would fail the StreamIdGuard
    // check for event-shaped children or silently scatter them onto an unpredictable stream.
    var composite = new _testComposite(new _innerEvent("only"));
    var source = _sourceEnvelopeWithoutAggregateId();
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    var child = result.Children.Single();
    await Assert.That(child.StreamId).IsEqualTo(source.MessageId.Value)
      .Because("with no AggregateId on the source hop, _extractStreamId must fall back to the source envelope's own MessageId.");
  }

  [Test]
  public async Task TryExpand_SourceHasNoHopsAtAll_ChildStreamIdFallsBackToSourceMessageIdAsync() {
    // A hops-less source (Hops is null) must hit the exact same fallback as a present-but-empty
    // AggregateId — the null-conditional walk over Hops must not throw.
    var composite = new _testComposite(new _innerEvent("only"));
    var source = _sourceEnvelopeWithoutHops();
    var sp = _provider();

    var result = CompositeInboxFanout.TryExpand(composite, source, sp);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    var child = result.Children.Single();
    await Assert.That(child.StreamId).IsEqualTo(source.MessageId.Value);
  }

  private static ServiceProvider _provider() =>
    new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new _fakeSerializer())
      .BuildServiceProvider();

  private static MessageEnvelope<JsonElement> _sourceEnvelopeWithoutAggregateId() {
    return new MessageEnvelope<JsonElement> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        // No AggregateId metadata — the fallback under test.
        Metadata = null,
      }],
      SourceServiceId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
      SourceCommitSequence = 42,
    };
  }

  private static MessageEnvelope<JsonElement> _sourceEnvelopeWithoutHops() {
    return new MessageEnvelope<JsonElement> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Hops = [],
      SourceServiceId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
      SourceCommitSequence = 42,
    };
  }

  /// <summary>Minimal serializer: records the payload's runtime AQN as MessageType.</summary>
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

  private sealed class _testComposite : ICompositeEvent {
    public _testComposite(params IEvent[] inner) {
      _inner = inner;
    }
    private readonly IEvent[] _inner;
    public int MaxInnerEventsAllowed => 10_000;
    public IEnumerable<IMessage> InnerEvents => _inner;
  }
}
