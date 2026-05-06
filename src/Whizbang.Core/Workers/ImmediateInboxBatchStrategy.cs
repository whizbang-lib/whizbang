using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Pass-through <see cref="IInboxBatchStrategy"/> — no batching. Each
/// <see cref="AppendAsync"/> invokes the bulk flush callback immediately with a
/// single-element array.
/// </summary>
/// <remarks>
/// <para>
/// Slice 7 of plans/pump-then-process.md (Half A). The default registration is
/// <see cref="SlidingWindowInboxBatchStrategy"/>; opt to this strategy via
/// <c>services.AddWhizbangInboxStrategy&lt;ImmediateInboxBatchStrategy&gt;()</c> for
/// low-throughput tenants or strict-ordering scenarios where the sliding window adds
/// latency without batching benefit.
/// </para>
/// </remarks>
/// <docs>internals/inbox-batch-strategy</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/InboxBatchStrategyRegistrationTests.cs</tests>
public sealed class ImmediateInboxBatchStrategy : IInboxBatchStrategy {
  private readonly InboxBulkFlushCallback _flush;
  private int _disposed;

  /// <summary>Creates the strategy with the given flush callback.</summary>
  public ImmediateInboxBatchStrategy(InboxBulkFlushCallback flush) {
    ArgumentNullException.ThrowIfNull(flush);
    _flush = flush;
  }

  /// <inheritdoc />
  public async ValueTask AppendAsync(InboxMessage message, CancellationToken cancellationToken = default) {
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
