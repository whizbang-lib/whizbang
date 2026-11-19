// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// <see cref="StreamIdentity"/>: the one reader and writer of the hop that names the stream a message is stored on.
/// The producer's outbox and every receiver read the same hop with it, so a consumer stores a message on the stream its
/// producer stored it on (#1798).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamIdentity.cs</code-under-test>
[Category("Messaging")]
public class StreamIdentityTests {

  /// <summary>
  /// A hop whose metadata carries no parsable stream yields nothing rather than something: guessing a stream puts the
  /// event on a stream nothing else writes to, while the caller's message-id fallback at least keeps it on its own.
  /// </summary>
  [Test]
  public async Task FromHopMetadata_NoParsableStream_YieldsNothingAsync() {
    var withoutTheKey = new Dictionary<string, JsonElement> { ["SomethingElse"] = JsonElement.Parse("\"" + Guid.CreateVersion7() + "\"") };
    var withANonStringValue = new Dictionary<string, JsonElement> { [StreamIdentity.HOP_KEY] = JsonElement.Parse("42") };
    var withAnUnparsableString = new Dictionary<string, JsonElement> { [StreamIdentity.HOP_KEY] = JsonElement.Parse("\"not-a-guid\"") };

    await Assert.That(StreamIdentity.FromHopMetadata(null)).IsNull();
    await Assert.That(StreamIdentity.FromHopMetadata(withoutTheKey)).IsNull()
      .Because("metadata that never named a stream has none to give");
    await Assert.That(StreamIdentity.FromHopMetadata(withANonStringValue)).IsNull()
      .Because("a number is not a stream id");
    await Assert.That(StreamIdentity.FromHopMetadata(withAnUnparsableString)).IsNull()
      .Because("a string that is not a GUID falls through to the caller's fallback");
  }

  /// <summary>The writer and the reader agree: what <see cref="StreamIdentity.HopMetadata"/> writes is read back.</summary>
  [Test]
  public async Task HopMetadata_IsReadBackAsync() {
    var streamId = Guid.CreateVersion7();

    var metadata = StreamIdentity.HopMetadata(streamId);

    await Assert.That(StreamIdentity.FromHopMetadata(metadata)).IsEqualTo(streamId);
    await Assert.That(metadata.Keys.Single()).IsEqualTo("AggregateId")
      .Because("the key is on the wire; every envelope already in flight or at rest names its stream under it");
  }

  /// <summary>Only the first hop names the stream; a later hop's metadata is journey history.</summary>
  [Test]
  public async Task FromHops_ReadsTheFirstHopOnlyAsync() {
    var first = Guid.CreateVersion7();
    var later = Guid.CreateVersion7();
    var envelope = _envelope(MessageId.New(), _hop(StreamIdentity.HopMetadata(first)), _hop(StreamIdentity.HopMetadata(later)));
    var laterOnly = _envelope(MessageId.New(), _hop(null), _hop(StreamIdentity.HopMetadata(later)));

    await Assert.That(StreamIdentity.FromHops(envelope)).IsEqualTo(first);
    await Assert.That(StreamIdentity.FromHops(laterOnly)).IsNull();
  }

  /// <summary>
  /// A message whose first hop names no stream, or that has no hops at all (a producer that wrote a null hop list
  /// yields an envelope whose hops are null), is stored on its own message id, so every message has a stream.
  /// </summary>
  [Test]
  public async Task Of_WithoutAHopStream_IsTheMessageIdAsync() {
    var named = Guid.CreateVersion7();
    var messageId = MessageId.New();

    await Assert.That(StreamIdentity.Of(_envelope(MessageId.New(), _hop(StreamIdentity.HopMetadata(named))))).IsEqualTo(named);
    await Assert.That(StreamIdentity.Of(_envelope(messageId, _hop(null)))).IsEqualTo(messageId.Value);
    await Assert.That(StreamIdentity.Of(_envelope(messageId))).IsEqualTo(messageId.Value);
    await Assert.That(StreamIdentity.Of(new MessageEnvelope<JsonElement>(messageId, JsonElement.Parse("{}"), hops: null!)))
      .IsEqualTo(messageId.Value);
  }

  /// <summary>
  /// A composite's own stream is the one its hop names, else its own <c>[StreamId]</c>, else its envelope's id; an empty
  /// <c>[StreamId]</c> names nothing.
  /// </summary>
  [Test]
  public async Task OfComposite_PrefersTheHopThenItsOwnStreamThenTheEnvelopeIdAsync() {
    var onHop = Guid.CreateVersion7();
    var own = Guid.CreateVersion7();
    var hopless = _envelope(MessageId.New(), _hop(null));
    var extractor = new FixedExtractor(own);

    await Assert.That(StreamIdentity.OfComposite(new ProbeComposite { StreamId = own }, _envelope(MessageId.New(), _hop(StreamIdentity.HopMetadata(onHop))), null))
      .IsEqualTo(onHop).Because("a dispatched composite names its stream on its hop, as every message does");
    await Assert.That(StreamIdentity.OfComposite(new ProbeComposite { StreamId = own }, hopless, null))
      .IsEqualTo(own).Because("a bundle built by hand names no hop stream, but its own [StreamId] still names one");
    await Assert.That(StreamIdentity.OfComposite(new UnbasedComposite(), hopless, extractor))
      .IsEqualTo(own).Because("a composite not built on the base names its stream through the generated extractor");
    await Assert.That(StreamIdentity.OfComposite(new ProbeComposite(), hopless, null))
      .IsEqualTo(hopless.MessageId.Value).Because("with no stream anywhere, the envelope's id is the last resort");
    await Assert.That(StreamIdentity.OfComposite(new UnbasedComposite(), hopless, new FixedExtractor(Guid.Empty)))
      .IsEqualTo(hopless.MessageId.Value).Because("an empty stream id names no stream");
  }

  /// <summary>A child's stream is the one its bundle recorded, else the one its payload names, else its composite's.</summary>
  [Test]
  public async Task OfFanoutChild_PrefersRecordedThenPayloadThenCompositeAsync() {
    var recorded = Guid.CreateVersion7();
    var named = Guid.CreateVersion7();
    var composite = Guid.CreateVersion7();
    var source = _envelope(MessageId.New(), _hop(StreamIdentity.HopMetadata(composite)));
    var bundle = new ProbeComposite();
    var payload = new object();
    var extractor = new FixedExtractor(named);

    await Assert.That(StreamIdentity.OfFanoutChild(recorded, payload, bundle, source, extractor)).IsEqualTo(recorded);
    await Assert.That(StreamIdentity.OfFanoutChild(Guid.Empty, payload, bundle, source, extractor)).IsEqualTo(named)
      .Because("an empty recorded stream is a slot the producer could not fill, not a stream");
    await Assert.That(StreamIdentity.OfFanoutChild(null, payload, bundle, source, extractor)).IsEqualTo(named);
    await Assert.That(StreamIdentity.OfFanoutChild(null, payload: null, bundle, source, extractor)).IsEqualTo(composite)
      .Because("a raw child has no typed payload to read; it takes its composite's stream");
    await Assert.That(StreamIdentity.OfFanoutChild(null, payload, bundle, source, streamIds: null)).IsEqualTo(composite);
  }

  /// <summary>A producer outside the dispatcher names the payload's stream on its hop, or nothing when it names none.</summary>
  [Test]
  public async Task HopMetadataFor_NamesThePayloadsStreamOrNothingAsync() {
    var named = Guid.CreateVersion7();

    await Assert.That(StreamIdentity.FromHopMetadata(StreamIdentity.HopMetadataFor(new object(), new FixedExtractor(named)))).IsEqualTo(named);
    await Assert.That(StreamIdentity.HopMetadataFor(new object(), new FixedExtractor(Guid.Empty))).IsNull()
      .Because("an empty stream id names no stream, so the hop names none and the receiver keeps the message id");
    await Assert.That(StreamIdentity.HopMetadataFor(new object(), streamIds: null)).IsNull()
      .Because("the generated registry finds no [StreamId] on a plain object");
  }

  private sealed class ProbeComposite : Whizbang.Core.Minting.CompositeEventBase;

  private sealed class UnbasedComposite : Whizbang.Core.Minting.ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => [];
  }

  private sealed class FixedExtractor(Guid streamId) : IStreamIdExtractor {
    public Guid? ExtractStreamId(object message, Type messageType) => message is ProbeComposite ? null : streamId;
  }

  private static MessageHop _hop(IReadOnlyDictionary<string, JsonElement>? metadata) => new() {
    Type = HopType.Current,
    Timestamp = DateTimeOffset.UnixEpoch,
    ServiceInstance = ServiceInstanceInfo.Unknown,
    Metadata = metadata,
  };

  private static MessageEnvelope<JsonElement> _envelope(MessageId messageId, params MessageHop[] hops) => new() {
    MessageId = messageId,
    Payload = JsonElement.Parse("{}"),
    Hops = [.. hops],
    DispatchContext = new MessageDispatchContext {
      Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox,
      Source = MessageSource.Outbox,
    },
  };
}
