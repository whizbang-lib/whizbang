using Whizbang.Core.Configuration;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Producer-side guard against writing rows whose <c>stream_id</c> is
/// <see cref="System.Guid.Empty"/>. When
/// <see cref="EmptyStreamIdPolicy.Reject"/> is in effect, throws
/// <see cref="EmptyStreamIdException"/> at <c>StoreOutboxMessagesAsync</c> /
/// <c>StoreInboxMessagesAsync</c> time so the producer's commit fails loud and
/// the bug is surfaced at the producer rather than spinning silently in
/// <c>wh_outbox</c> / <c>wh_inbox</c>.
/// </summary>
/// <remarks>
/// A production forensic investigation motivated this guard: see
/// <see cref="EmptyStreamIdPolicy"/> for the full motivation. This is one of
/// two defenses; <c>StreamIdCoalescer</c> in
/// <c>Whizbang.Data.EFCore.Postgres</c> is the consumer-side recovery for rows
/// that already landed.
/// </remarks>
/// <docs>operations/configuration/empty-stream-id-policy</docs>
public static class EmptyStreamIdGuard {

  /// <summary>
  /// Per-row primitive. Throws <see cref="EmptyStreamIdException"/> when
  /// <paramref name="policy"/> is <see cref="EmptyStreamIdPolicy.Reject"/> and
  /// <paramref name="streamId"/> is <see cref="System.Guid.Empty"/>. NULL
  /// passes through (legitimate singleton-stream marker); real UUIDs pass
  /// through.
  /// </summary>
  public static void ThrowIfEmpty(
    System.Guid messageId,
    string messageType,
    System.Guid? streamId,
    EmptyStreamIdPolicy policy) {
    if (policy != EmptyStreamIdPolicy.Reject) {
      return;
    }
    if (streamId == System.Guid.Empty) {
      throw new EmptyStreamIdException(messageId, messageType);
    }
  }

  /// <summary>
  /// Batch overload for outbox storage. Fails fast on the first offending row.
  /// </summary>
  public static void ThrowIfAnyHasEmptyStreamId(OutboxMessage[] messages, EmptyStreamIdPolicy policy) {
    if (policy != EmptyStreamIdPolicy.Reject) {
      return;
    }
    foreach (var m in messages) {
      ThrowIfEmpty(m.MessageId, m.MessageType, m.StreamId, policy);
    }
  }

  /// <summary>
  /// Batch overload for inbox storage. Fails fast on the first offending row.
  /// </summary>
  public static void ThrowIfAnyHasEmptyStreamId(InboxMessage[] messages, EmptyStreamIdPolicy policy) {
    if (policy != EmptyStreamIdPolicy.Reject) {
      return;
    }
    foreach (var m in messages) {
      ThrowIfEmpty(m.MessageId, m.MessageType, m.StreamId, policy);
    }
  }
}
