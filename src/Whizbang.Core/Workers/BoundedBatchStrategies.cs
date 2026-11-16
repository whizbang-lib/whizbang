// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Text.Json;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Who is appending to a batch strategy, for the batch buffer: a name for its warnings, and how to ask whether the
/// caller's scope holds a database transaction open.
/// </summary>
/// <param name="Caller">Names the caller in the transaction-overshoot warning.</param>
/// <param name="HoldsOpenTransaction">
/// Answers whether the caller's scope has a database transaction open; asked only when the append would wait. Null
/// when the caller cannot tell, which counts as no transaction.
/// </param>
/// <docs>messaging/outbox-pattern#bounded-buffer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/BoundedBatchStrategyTests.cs</tests>
public readonly record struct BatchAppendContext(string Caller, Func<bool>? HoldsOpenTransaction) {
  /// <summary>An append from a caller that can say who it is but not whether it holds a transaction open.</summary>
  /// <param name="caller">Names the caller.</param>
  /// <returns>The context.</returns>
  public static BatchAppendContext Anonymous(string caller) => new(caller, null);
}

/// <summary>The size an append takes in the batch buffer: the payload's serialized JSON, as it will be written.</summary>
internal static class BatchMessageSize {
  /// <summary>The byte length of the envelope's payload, read from the element without serializing it again.</summary>
  public static long Of(IMessageEnvelope<JsonElement> envelope) =>
    envelope.Payload.ValueKind == JsonValueKind.Undefined ? 0 : JsonMarshal.GetRawUtf8Value(envelope.Payload).Length;
}

/// <summary>
/// Bounds what the outbox batch strategy it wraps holds in memory (#2220). Every registered
/// <see cref="IOutboxBatchStrategy"/> is wrapped in one at registration, so the bound applies whichever strategy is
/// chosen; an append waits for room while the process-wide buffer is full.
/// </summary>
/// <docs>messaging/outbox-pattern#bounded-buffer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/BoundedBatchStrategyTests.cs</tests>
public sealed class BoundedOutboxBatchStrategy : IOutboxBatchStrategy {
  private const string DEFAULT_CALLER = "An outbox append";
  private readonly BatchBuffer _buffer;

  internal BoundedOutboxBatchStrategy(IOutboxBatchStrategy inner, BatchBuffer buffer) {
    ArgumentNullException.ThrowIfNull(inner);
    ArgumentNullException.ThrowIfNull(buffer);
    Inner = inner;
    _buffer = buffer;
  }

  /// <summary>The strategy that batches and flushes what this admits.</summary>
  public IOutboxBatchStrategy Inner { get; }

  /// <inheritdoc />
  public ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
    AppendAsync(message, BatchAppendContext.Anonymous(DEFAULT_CALLER), cancellationToken);

  /// <summary>
  /// Appends <paramref name="message"/> once the buffer has room for it, telling the buffer who is appending.
  /// </summary>
  /// <param name="message">The message.</param>
  /// <param name="context">Who is appending, and whether their scope holds a transaction open.</param>
  /// <param name="cancellationToken">Ends a wait for room.</param>
  /// <returns>Completes when the message is buffered by the inner strategy.</returns>
  public async ValueTask AppendAsync(OutboxMessage message, BatchAppendContext context, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await _buffer.AdmitAsync(message, BatchMessageSize.Of(message.Envelope), WorkCoordinatorGate.IsInteractiveCaller(), context, cancellationToken)
      .ConfigureAwait(false);
    try {
      await Inner.AppendAsync(message, cancellationToken).ConfigureAwait(false);
    } catch (Exception) {
      // Refused or not buffered: the room it took is given back now, since no flush will ever release it.
      _buffer.ReleaseOne(message);
      throw;
    }
  }

  /// <inheritdoc />
  public Task FlushAndStopAsync(CancellationToken cancellationToken = default) {
    _buffer.Stop();
    return Inner.FlushAndStopAsync(cancellationToken);
  }

  /// <inheritdoc />
  public ValueTask DisposeAsync() {
    _buffer.Stop();
    return Inner.DisposeAsync();
  }
}

/// <summary>
/// Bounds what the inbox batch strategy it wraps holds in memory (#2220), sharing the outbox's process-wide buffer. An
/// append waits while the buffer is full, so the receive loop stops pulling from the broker until flushes make room.
/// </summary>
/// <docs>messaging/inbox-pattern#bounded-buffer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/BoundedBatchStrategyTests.cs</tests>
public sealed class BoundedInboxBatchStrategy : IInboxBatchStrategy {
  private const string CALLER = "An inbox append";
  private readonly BatchBuffer _buffer;

  internal BoundedInboxBatchStrategy(IInboxBatchStrategy inner, BatchBuffer buffer) {
    ArgumentNullException.ThrowIfNull(inner);
    ArgumentNullException.ThrowIfNull(buffer);
    Inner = inner;
    _buffer = buffer;
  }

  /// <summary>The strategy that batches and flushes what this admits.</summary>
  public IInboxBatchStrategy Inner { get; }

  /// <inheritdoc />
  public async ValueTask AppendAsync(InboxMessage message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await _buffer.AdmitAsync(message, BatchMessageSize.Of(message.Envelope), WorkCoordinatorGate.IsInteractiveCaller(),
      BatchAppendContext.Anonymous(CALLER), cancellationToken).ConfigureAwait(false);
    try {
      await Inner.AppendAsync(message, cancellationToken).ConfigureAwait(false);
    } catch (Exception) {
      _buffer.ReleaseOne(message);
      throw;
    }
  }

  /// <inheritdoc />
  public Task FlushAndStopAsync(CancellationToken cancellationToken = default) {
    _buffer.Stop();
    return Inner.FlushAndStopAsync(cancellationToken);
  }

  /// <inheritdoc />
  public ValueTask DisposeAsync() {
    _buffer.Stop();
    return Inner.DisposeAsync();
  }
}
