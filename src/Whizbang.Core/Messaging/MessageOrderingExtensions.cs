using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Canonical message-id ordering for any sequence containing more than one message.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Invariant.</strong> Every component that handles more than one message MUST sort by
/// <c>MessageId</c> (UUIDv7 = chronological) before doing anything with the sequence.
/// Insertion order, channel arrival order, parallel-handler completion order, transport delivery
/// order — none of these can be trusted to match logical time. The only ordering we trust is the
/// monotonic time encoding in UUIDv7.
/// </para>
/// <para>
/// Lock the invariant per touchpoint with a regression test that feeds shuffled input and asserts
/// the component processes/yields in <c>MessageId</c>-ascending order.
/// </para>
/// </remarks>
/// <docs>internals/ordering-invariant</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MessageOrderingExtensionsTests.cs</tests>
public static class MessageOrderingExtensions {
  /// <summary>
  /// Sorts a sequence of message envelopes by <see cref="IMessageEnvelope.MessageId"/> ascending.
  /// </summary>
  public static IOrderedEnumerable<T> OrderByMessageId<T>(this IEnumerable<T> source)
      where T : IMessageEnvelope
    => source.OrderBy(static x => x.MessageId.Value);

  /// <summary>
  /// Sorts a sequence of <see cref="OutboxWork"/> by <see cref="IHasMessageIdAndStatus.MessageId"/> ascending.
  /// </summary>
  public static IOrderedEnumerable<OutboxWork> OrderByMessageId(this IEnumerable<OutboxWork> source)
    => source.OrderBy(static x => x.MessageId);

  /// <summary>
  /// Sorts a sequence of <see cref="InboxWork"/> by <see cref="IHasMessageIdAndStatus.MessageId"/> ascending.
  /// </summary>
  public static IOrderedEnumerable<InboxWork> OrderByMessageId(this IEnumerable<InboxWork> source)
    => source.OrderBy(static x => x.MessageId);

  /// <summary>
  /// Sorts a sequence of <see cref="OutboxBatchRow"/> by <see cref="OutboxBatchRow.MessageId"/> ascending.
  /// </summary>
  public static IOrderedEnumerable<OutboxBatchRow> OrderByMessageId(this IEnumerable<OutboxBatchRow> source)
    => source.OrderBy(static x => x.MessageId);

  /// <summary>
  /// Sorts a sequence of <see cref="InboxBatchRow"/> by <see cref="InboxBatchRow.MessageId"/> ascending.
  /// </summary>
  public static IOrderedEnumerable<InboxBatchRow> OrderByMessageId(this IEnumerable<InboxBatchRow> source)
    => source.OrderBy(static x => x.MessageId);
}
