// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
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
/// on the composite's (<see cref="CompositeInboxFanout"/>).
/// </para>
/// <para>
/// There were three byte-for-byte copies of the hop read (the outbox, the receive builder and the fan-out) and a
/// fourth reader in the dispatcher's cascade path. Every site now calls this class.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/stream-identity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIdentityTests.cs</tests>
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
