// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Runs <see cref="Disposition.Compact"/>: folds the detail a destruction is about to remove into a summary the developer
/// keeps (for example a carry-forward event), before the detail is discarded (#1735). Compaction is developer-supplied in
/// 1.0, so there is no built-in handler; register one in DI. With none registered, a <see cref="Disposition.Compact"/>
/// keeps the data and logs why.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#dispositions</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/StreamPrefixTruncatorDispositionTests.cs</tests>
public interface ICompactionHandler {
  /// <summary>
  /// Folds the detail <paramref name="context"/> describes into the developer's summary. Awaited before the detail is
  /// discarded; anything it writes must be durable when it returns. A throw keeps the detail.
  /// </summary>
  /// <param name="context">What is about to be removed: the stream and how far, or the event batch.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes when the summary is durable.</returns>
  ValueTask CompactAsync(DestructionContext context, CancellationToken cancellationToken = default);
}
