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
