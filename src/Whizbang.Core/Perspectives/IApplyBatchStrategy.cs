// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// When a set-based batch's prepared rows are written: the flush-policy seam of the set-based write mode.
/// </summary>
/// <remarks>
/// <para>
/// The worker prepares every pair of a batch, then hands the prepared writes of each DbContext to this policy with that
/// context's write sink, and completes each pair with the outcome the policy returns. The default
/// (<see cref="FlushEveryBatchStrategy"/>) writes them at once, so a batch's rows are durable before its pairs complete.
/// </para>
/// <para>
/// A policy decides only when the writes reach the sink, never how they are written: it returns one outcome per write,
/// at the same index, as the sink does. A write-behind policy (#2247) is a later mode built on this seam.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#flush-policy</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/FlushEveryBatchStrategyTests.cs</tests>
public interface IApplyBatchStrategy {
  /// <summary>Writes a batch's prepared rows through <paramref name="sink"/>, when the policy decides.</summary>
  /// <param name="sink">The write sink of the writes' DbContext.</param>
  /// <param name="writes">The prepared writes, in batch order.</param>
  /// <param name="cancellationToken">Cancels the flush.</param>
  /// <returns>One outcome per write, at the same index.</returns>
  Task<IReadOnlyList<PerspectiveWriteOutcome>> FlushAsync(
      IPerspectiveWriteSink sink, IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default);
}

/// <summary>The default flush policy: every batch's rows are written as soon as the batch is prepared.</summary>
/// <docs>fundamentals/perspectives/write-modes#flush-policy</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/FlushEveryBatchStrategyTests.cs</tests>
public sealed class FlushEveryBatchStrategy : IApplyBatchStrategy {
  /// <inheritdoc/>
  public Task<IReadOnlyList<PerspectiveWriteOutcome>> FlushAsync(
      IPerspectiveWriteSink sink, IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(sink);
    ArgumentNullException.ThrowIfNull(writes);
    return sink.WriteAsync(writes, cancellationToken);
  }
}
