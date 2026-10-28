// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The one place that decides which stream a message is stored on, wherever it is stored: the producer's outbox, a
/// consumer's inbox on receive, dead-letter recovery, and every child a composite fans out into (#1798).
/// </summary>
/// <remarks>
/// <para>
/// A producer names the stream on the message's first hop (<see cref="HOP_KEY"/>, the key is historical) and stores
/// the message on that stream. Every receiver reads the same hop, so the stream a consumer stores a message on is the
/// stream its producer stored it on. A message whose hop names no stream is stored on its own message id, on both
/// sides.
/// </para>
/// <para>
/// A composite is transport packaging: its children are stored on the streams their producers stored them on, never
/// on the composite's. A child's stream is, in order, the one the bundle recorded for it, the one its payload's
/// <c>[StreamId]</c> names, and the composite's own stream (<see cref="OfComposite"/>). A composite whose hop named no
/// stream once filed its children under its envelope's id, a random stream no row is keyed by: the integrity
/// re-delivery bundle was built that way, so every event it repaired missed the row it is about.
/// </para>
/// <para>
/// There were three byte-for-byte copies of the hop read (the outbox, the receive builder and the fan-out) and a
/// fourth reader in the dispatcher's cascade path. Every site now calls this class. <c>StreamResolutionParityTests</c>
/// holds every delivery shape against it, and <c>StreamResolutionSourceGuardTests</c> fails when a new site reads the
/// hop key itself or builds an inbox row the parity table does not cover.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/stream-identity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIdentityTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamResolutionParityTests.cs:StoredStream_IsTheStreamTheProducerStoredItOnAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamResolutionSitesTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Messaging/StreamResolutionSourceGuardTests.cs</tests>
internal static class StreamIdentity {
  /// <summary>The hop metadata key a producer names a message's stream under.</summary>
  internal const string HOP_KEY = "AggregateId";

  /// <summary>The stream a hop's metadata names, or null when it names none (no key, not a string, not a GUID).</summary>
  /// <param name="metadata">A hop's metadata.</param>
  /// <returns>The stream, or null.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIdentityTests.cs:FromHopMetadata_NoParsableStream_YieldsNothingAsync</tests>
  internal static Guid? FromHopMetadata(IReadOnlyDictionary<string, JsonElement>? metadata) =>
    metadata is not null
    && metadata.TryGetValue(HOP_KEY, out var element)
    && element.ValueKind == JsonValueKind.String
    && Guid.TryParse(element.GetString(), out var streamId)
      ? streamId
      : null;

  /// <summary>The stream a message's first hop names, or null.</summary>
  /// <param name="envelope">The message.</param>
  /// <returns>The stream, or null.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIdentityTests.cs:FromHops_ReadsTheFirstHopOnlyAsync</tests>
  internal static Guid? FromHops(IMessageEnvelope envelope) =>
    FromHopMetadata(envelope.Hops is { Count: > 0 } hops ? hops[0].Metadata : null);

  /// <summary>
  /// The stream a message is stored on: the one its first hop names, or its own message id when it names none, so
  /// every message has a stream. The producer's outbox and every receiver call this, so both sides agree.
  /// </summary>
  /// <param name="envelope">The message.</param>
  /// <returns>The stream.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIdentityTests.cs:Of_WithoutAHopStream_IsTheMessageIdAsync</tests>
  internal static Guid Of(IMessageEnvelope envelope) => FromHops(envelope) ?? envelope.MessageId.Value;

  /// <summary>
  /// A composite's own stream: the one its hop names, else the one its own <c>[StreamId]</c> names, else its
  /// envelope's id. A bundle built by hand (not dispatched) carries no hop stream, and its envelope id names nothing.
  /// </summary>
  /// <param name="composite">The composite.</param>
  /// <param name="source">The composite's envelope.</param>
  /// <param name="streamIds">Reads a <c>[StreamId]</c>; when null, only <see cref="CompositeEventBase.StreamId"/> is read.</param>
  /// <returns>The stream.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIdentityTests.cs:OfComposite_PrefersTheHopThenItsOwnStreamThenTheEnvelopeIdAsync</tests>
  internal static Guid OfComposite(ICompositeEvent composite, IMessageEnvelope source, IStreamIdExtractor? streamIds) =>
    FromHops(source)
    ?? _named(composite, streamIds)
    ?? _nonEmpty((composite as CompositeEventBase)?.StreamId)
    ?? source.MessageId.Value;

  /// <summary>
  /// The stream a composite's child is stored on: the one the bundle recorded for it, else the one its payload names,
  /// else the composite's own (<see cref="OfComposite"/>).
  /// </summary>
  /// <param name="recorded">The stream the bundle recorded for this child, if any.</param>
  /// <param name="payload">The child's typed payload, when there is one.</param>
  /// <param name="composite">The composite.</param>
  /// <param name="source">The composite's envelope.</param>
  /// <param name="streamIds">Reads a <c>[StreamId]</c>.</param>
  /// <returns>The stream.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIdentityTests.cs:OfFanoutChild_PrefersRecordedThenPayloadThenCompositeAsync</tests>
  internal static Guid OfFanoutChild(
      Guid? recorded, object? payload, ICompositeEvent composite, IMessageEnvelope source, IStreamIdExtractor? streamIds) =>
    _nonEmpty(recorded) ?? (payload is null ? null : _named(payload, streamIds)) ?? OfComposite(composite, source, streamIds);

  private static Guid? _named(object message, IStreamIdExtractor? streamIds) =>
    _nonEmpty(streamIds?.ExtractStreamId(message, message.GetType()));

  private static Guid? _nonEmpty(Guid? streamId) => streamId is { } id && id != Guid.Empty ? id : null;

  /// <summary>
  /// Hop metadata naming the stream <paramref name="message"/>'s <c>[StreamId]</c> names, or null when it names none, for
  /// a producer that builds an envelope outside the dispatcher (which writes the hop itself).
  /// </summary>
  /// <param name="message">The message.</param>
  /// <param name="streamIds">Reads a <c>[StreamId]</c>; the generated registry's when null.</param>
  /// <returns>The metadata, or null.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIdentityTests.cs:HopMetadataFor_NamesThePayloadsStreamOrNothingAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamResolutionParityTests.cs</tests>
  internal static Dictionary<string, JsonElement>? HopMetadataFor(object message, IStreamIdExtractor? streamIds) =>
    _named(message, streamIds ?? Registry.StreamIdExtractorRegistry.GetComposite()) is { } streamId ? HopMetadata(streamId) : null;

  /// <summary>Hop metadata naming <paramref name="streamId"/>, read back by <see cref="FromHopMetadata"/>.</summary>
  /// <param name="streamId">The stream.</param>
  /// <returns>The metadata.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIdentityTests.cs:HopMetadata_IsReadBackAsync</tests>
  internal static Dictionary<string, JsonElement> HopMetadata(Guid streamId) {
    // AOT-safe: a GUID's text needs no escaping, so the JSON string is built and parsed, not serialized.
    using var document = JsonDocument.Parse("\"" + streamId.ToString("D") + "\"");
    return new Dictionary<string, JsonElement> { [HOP_KEY] = document.RootElement.Clone() };
  }
}
