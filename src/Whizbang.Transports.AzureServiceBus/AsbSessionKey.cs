using System.Globalization;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Decides the Service Bus session id for an outbound message. Single source of truth for both the
/// single-message and batch publish paths.
/// </summary>
/// <remarks>
/// <para>
/// A session-enabled entity REJECTS a message whose session id is null — "Session id is null. —
/// Session enabled entity doesn't allow a message whose session identifier is null." The broker
/// dead-letters it before any consumer sees it. Control-plane broadcasts
/// (<c>IntegrityCheckpoint</c> and friends) carry no stream, so a stream-only rule left them
/// unpublishable on exactly the entities whose ordering guarantees everything else depends on.
/// </para>
/// <para>
/// Streamed messages keep using the stream id: session id IS the ordering key, and per-stream FIFO
/// depends on it.
/// </para>
/// <para>
/// Streamless messages are spread across a BOUNDED set of synthetic sessions rather than getting one
/// session each. Three forces pin that choice:
/// </para>
/// <list type="bullet">
///   <item><description>
///     One shared constant would satisfy the broker and then funnel every broadcast through a single
///     session — one consumer, fully serialized. A rejection bug traded for a throughput collapse.
///   </description></item>
///   <item><description>
///     One session PER MESSAGE would maximize parallelism but churn sessions without limit. Session
///     acceptance is a finite resource on this transport and has already produced a starvation
///     deadlock here once; unbounded session creation is the same failure waiting to recur.
///   </description></item>
///   <item><description>
///     Batching requires uniform session ids within a <c>ServiceBusMessageBatch</c>. Per-message
///     sessions would force one batch per message and turn a single broadcast send into N round
///     trips. Buckets keep batching intact — items sharing a bucket still batch together.
///   </description></item>
/// </list>
/// <para>
/// The key is also NOT parseable as a <see cref="Guid"/>, because inbound paths recover the stream id
/// via <c>Guid.TryParse(msg.SessionId, ...)</c>. A GUID-shaped fallback would invent a stream
/// association for a message that has none.
/// </para>
/// </remarks>
/// <docs>transports/azure-service-bus</docs>
internal static class AsbSessionKey {

  /// <summary>
  /// Prefix marking a session that stands in for "this message has no stream". Non-hex, so the key
  /// can never parse as a <see cref="Guid"/> — that is what stops streamless messages being
  /// mistaken for streamed ones on the way back in.
  /// </summary>
  internal const string STREAMLESS_PREFIX = "nostream-";

  /// <summary>
  /// How many synthetic sessions streamless messages are spread across. Bounded on purpose: high
  /// enough that broadcasts are not serialized behind one another, low enough that session
  /// acceptance stays cheap and batches stay meaningfully full.
  /// </summary>
  internal const int STREAMLESS_BUCKETS = 16;

  /// <summary>
  /// The session id to stamp on an outbound message.
  /// </summary>
  /// <param name="streamId">The stream this message belongs to, if any.</param>
  /// <param name="messageId">The message's own id, used to pick a bucket when there is no stream.</param>
  /// <returns>
  /// The stream id for streamed messages; otherwise a bounded, deterministic synthetic session.
  /// Never <see langword="null"/> — that is the whole point.
  /// </returns>
  internal static string For(Guid? streamId, Guid messageId) {
    if (streamId is { } stream) {
      return stream.ToString();
    }

    // Bucket derived EXPLICITLY from the id's bytes rather than from GetHashCode. Two different
    // instances publishing the same message must land it in the same session, and a re-publish must
    // too, so the mapping has to be stable across processes and runtime versions. Guid.GetHashCode
    // happens to be byte-derived today, but that is an implementation detail — depending on it would
    // make session assignment silently version-fragile.
    Span<byte> bytes = stackalloc byte[16];
    _ = messageId.TryWriteBytes(bytes);
    var bucket = bytes[15] % STREAMLESS_BUCKETS;
    return STREAMLESS_PREFIX + bucket.ToString(CultureInfo.InvariantCulture);
  }
}
