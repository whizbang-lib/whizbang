// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Component.Tests;

/// <summary>
/// The Component suite's own proof that it runs: library code executes on a real thread, and the test
/// waits on the signal for the exact transition it asserts (the worker handing over its result), never
/// on a delay, a poll or a timeout.
/// </summary>
/// <remarks>
/// Component tests may start real workers and threads in one process, with no external infrastructure.
/// The test fixes every interleaving through signals. See docs/TEST-PROJECTS.md.
/// </remarks>
[Category("Component")]
public class ComponentSuiteTests {
  [Test]
  public async Task TrackedGuid_CreatedOnAWorkerThread_ReachesTheTestThroughItsHandOverSignalAsync() {
    var handedOver = new TaskCompletionSource<(TrackedGuid Id, int ThreadId)>(TaskCreationOptions.RunContinuationsAsynchronously);
    var worker = new Thread(() => handedOver.SetResult((TrackedGuid.New(), Environment.CurrentManagedThreadId))) {
      IsBackground = true,
      Name = "component-suite-worker"
    };

    worker.Start();
    var (id, workerThreadId) = await handedOver.Task;
    worker.Join();

    await Assert.That(workerThreadId).IsEqualTo(worker.ManagedThreadId);
    await Assert.That(id.IsTimeOrdered).IsTrue();
    await Assert.That(id.IsTracking).IsTrue();
  }
}
