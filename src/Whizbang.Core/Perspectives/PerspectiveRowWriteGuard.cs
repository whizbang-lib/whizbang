// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The last check before a generated perspective runner writes a row: a row is written only from a model an Apply
/// produced, or from the stored row or snapshot an apply continues from (#1677).
/// </summary>
/// <remarks>
/// <para>
/// A runner folds a stream onto a model. When the stream has no row yet it starts from a scaffolded default, so the
/// first Apply has something to read; that default is Apply input, not a model. A perspective that declines every
/// event of a stream (each Apply returns <see cref="ApplyResult{TModel}.None"/>) leaves the scaffold untouched, and
/// storing it puts a blank row in the table that reads as a real one: empty strings, zero counts, the earliest
/// timestamps. The runner tracks whether an Apply produced the model it is about to write and asks this guard first;
/// the guard refuses a write of a model no Apply produced and logs a warning naming the perspective and stream.
/// </para>
/// <para>
/// Every write path in the runner already declines such a batch before it reaches the write, so a warning here means
/// a write path that does not, which is a defect to report.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives-with-actions</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowWriteGuardTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/DeclinedStreamLeavesNoRowTests.cs</tests>
public static partial class PerspectiveRowWriteGuard {
  /// <summary>
  /// Whether the runner may write the row: true when an Apply produced the model, or it continues a stored row or
  /// snapshot. Otherwise logs a warning and returns false, and the runner writes nothing.
  /// </summary>
  /// <param name="producedByApply">Whether the model to be written came from an Apply, a stored row or a snapshot.</param>
  /// <param name="logger">The runner's logger, for the refusal.</param>
  /// <param name="perspectiveName">The perspective whose row would be written.</param>
  /// <param name="streamId">The stream whose row would be written.</param>
  /// <returns>Whether the write may go ahead.</returns>
  public static bool AllowsWrite(bool producedByApply, ILogger logger, string perspectiveName, Guid streamId) {
    ArgumentNullException.ThrowIfNull(logger);
    if (producedByApply) {
      return true;
    }
    LogUnproducedRowRefused(logger, perspectiveName, streamId);
    return false;
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "[diag.write.unproduced] {PerspectiveName} stream {StreamId}: refused to write a row no Apply produced; "
            + "the model is the scaffolded default the runner builds as Apply input, and storing it would create a blank row")]
  private static partial void LogUnproducedRowRefused(ILogger logger, string perspectiveName, Guid streamId);
}
