// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The default <see cref="IPerspectiveStateSource"/>: per perspective of a batch, one read of the rows of every stream
/// not known to be new, and for the rows found missing one purge-marker query and one history query.
/// </summary>
/// <remarks>
/// <para>
/// Each read is made once, by the first pair of the perspective that asks, through that pair's own store, purge
/// markers or event store; every other pair of the perspective waits for the same read. The pairs of a batch are
/// prepared by runners of the same generated type, so whichever asks first reads exactly what any other would.
/// </para>
/// <para>
/// A read that fails is logged and answered as unknown, so each pair reads its own row per stream and meets the
/// failure (and its explanation, such as a stored document no reader takes) exactly as the per-pair path does.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#state-source</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/BatchedPerspectiveStateSourceTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectivePrepareCompleteIntegrationTests.StateSource.cs</tests>
public sealed class BatchedPerspectiveStateSource : IPerspectiveStateSource {
  /// <summary>Creates the state source.</summary>
  /// <param name="logger">Logs a batched read that failed.</param>
  public BatchedPerspectiveStateSource(ILogger<BatchedPerspectiveStateSource>? logger = null) {
    _ = logger;
  }

  /// <inheritdoc/>
  public IPerspectiveBatchState BeginBatch(IReadOnlyList<PerspectiveStateRequest> pairs) => throw new NotImplementedException();
}
