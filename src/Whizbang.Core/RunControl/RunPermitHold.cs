// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.RunControl;

/// <summary>
/// One unit of work in hand, taken from the run permit with <see cref="WhizbangRunPermit.EnterAsync"/> (#2045). The
/// run-control path waits for every hold to be disposed before it releases what is left, so dispose it as soon as the
/// unit of work is finished. Disposing it releases the hold once; a second dispose does nothing.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunPermitTests.cs:Enter_WhileRunning_CountsTheHoldUntilItIsDisposedAsync</tests>
public sealed class RunPermitHold : IDisposable {
  private WhizbangRunPermit? _permit;

  internal RunPermitHold(WhizbangRunPermit permit) => _permit = permit;

  /// <summary>Releases the hold.</summary>
  public void Dispose() => Interlocked.Exchange(ref _permit, null)?.Release();
}
