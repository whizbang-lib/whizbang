// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Branch backfill for <see cref="SyncEventTracker"/>'s shared wait helper: every key the caller
/// listed as pending has already completed by the time the helper re-checks it, so nothing is left to
/// wait for.
/// </summary>
/// <remarks>
/// Each test constructs its own tracker and waiter registry; the class holds no process-global state.
/// </remarks>
public class SyncEventTrackerBranchCoverageTests {

  // The caller's pending list is a snapshot. When every key in it completes before the helper gets to
  // it, the helper must report success at once without registering a waiter (a registration nobody
  // will ever signal would only be cleaned up by TTL) and without starting the timeout.
  [Test]
  public async Task WaitForCompletion_EveryListedKeyAlreadyCompleted_ReturnsTrueWithoutRegisteringAWaiterAsync() {
    var tracker = new SyncEventTracker();
    var waiters = new ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, TaskCompletionSource<bool>>>();
    Guid[] keys = [Guid.CreateVersion7(), Guid.CreateVersion7()];
    var asked = new List<Guid>();

    var completed = await tracker.WaitForCompletionAsync(
      waiters,
      keys,
      key => {
        asked.Add(key);
        return false;
      },
      Guid.CreateVersion7(),
      TimeSpan.FromMinutes(5),
      CancellationToken.None);

    await Assert.That(completed).IsTrue()
      .Because("keys that completed between the caller's snapshot and the helper's check are done, not timed out");
    await Assert.That(asked).IsEquivalentTo(keys)
      .Because("each listed key is re-checked exactly once, and a completed key is not checked a second time");
    await Assert.That(waiters.Count).IsEqualTo(0)
      .Because("no waiter may be registered for a key that is no longer pending");
  }
}
