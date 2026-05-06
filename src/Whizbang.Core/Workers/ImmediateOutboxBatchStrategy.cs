using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Pass-through <see cref="IOutboxBatchStrategy"/> — no batching. Each
/// <see cref="AppendAsync"/> invokes the bulk flush callback immediately with a
/// single-element array.
/// </summary>
/// <remarks>
/// <para>
/// Slice 12 of plans/pump-then-process.md (Half B). The default registration is
/// <see cref="SlidingWindowOutboxBatchStrategy"/>; opt to this strategy via
/// <c>services.AddWhizbangOutboxStrategy&lt;ImmediateOutboxBatchStrategy&gt;()</c> for
/// low-throughput tenants or strict-ordering scenarios where the per-stream sliding
/// window adds latency without batching benefit.
/// </para>
/// <para>
/// Per-stream affinity is preserved trivially because each call is its own batch — there
/// is no within-window reordering to worry about. The producer's call order is the
/// flush callback's call order.
/// </para>
/// </remarks>
/// <docs>internals/outbox-batch-strategy</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OutboxBatchStrategyRegistrationTests.cs</tests>
public sealed class ImmediateOutboxBatchStrategy : IOutboxBatchStrategy {
  private readonly OutboxBulkFlushCallback _flush;
  private int _disposed;

  /// <summary>Creates the strategy with the given flush callback.</summary>
  public ImmediateOutboxBatchStrategy(OutboxBulkFlushCallback flush) {
    ArgumentNullException.ThrowIfNull(flush);
    _flush = flush;
  }

  /// <inheritdoc />
  public async ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    ArgumentNullException.ThrowIfNull(message);
    await _flush([message], cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public Task FlushAndStopAsync(CancellationToken cancellationToken = default) {
    Interlocked.Exchange(ref _disposed, 1);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask DisposeAsync() {
    Interlocked.Exchange(ref _disposed, 1);
    return ValueTask.CompletedTask;
  }
}
