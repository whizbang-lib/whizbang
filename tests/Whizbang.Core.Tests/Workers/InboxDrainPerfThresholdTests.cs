// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A stream's drain earns a debug perf line when it enqueued five or more rows or took more than
/// 100 ms; a small, fast drain stays quiet so the debug log is not one line per stream.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDrainWorker.cs</code-under-test>
public class InboxDrainPerfThresholdTests {
  [Test]
  [Arguments(4, 100.0, false)]
  [Arguments(5, 0.0, true)]
  [Arguments(0, 100.5, true)]
  [Arguments(0, 0.0, false)]
  public async Task IsInterestingDrain_LogsBigOrSlowDrainsOnlyAsync(int enqueued, double totalMs, bool expected) {
    await Assert.That(InboxDrainWorker.IsInterestingDrain(enqueued, totalMs)).IsEqualTo(expected);
  }
}
