// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.RunControl;
using Whizbang.Testing.Lifecycle;

namespace Whizbang.Testing.Tests.Lifecycle;

/// <summary>
/// The exit-path double (#2126) records every request, completes on the first, and touches nothing else: a test that
/// hand-builds a worker asserts on what the worker asked for.
/// </summary>
public class RecordingInstanceExitTests {
  [Test]
  public async Task BeforeAnyRequest_NothingIsUnderWayAsync() {
    var exit = new RecordingInstanceExit();

    await Assert.That(exit.Cause).IsNull();
    await Assert.That(exit.Report).IsNull();
    await Assert.That(exit.Requests).IsEmpty();
    await Assert.That(exit.Completion.IsCompleted).IsFalse();
  }

  [Test]
  public async Task EveryRequestIsRecorded_TheFirstIsTheCause_AndCompletesTheExitAsync() {
    var exit = new RecordingInstanceExit();
    var first = new InstanceExitRequest(ExitCause.ClusterEviction, "evicted");
    var second = new InstanceExitRequest(ExitCause.Crash, "later");

    await exit.ExitAsync(first, CancellationToken.None);
    exit.RequestExit(second);

    await Assert.That(exit.Requests).IsEquivalentTo([first, second], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(exit.Cause).IsEqualTo(ExitCause.ClusterEviction);
    await Assert.That(await exit.FirstRequest).IsSameReferenceAs(first);
    await Assert.That(exit.Completion.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  public async Task TheHostsStartAndStop_AreCountedAsync() {
    var exit = new RecordingInstanceExit();

    exit.OnHostStarting();
    await exit.OnHostStoppingAsync(CancellationToken.None);
    await exit.OnHostStoppingAsync(CancellationToken.None);

    await Assert.That(exit.HostStartingCalls).IsEqualTo(1);
    await Assert.That(exit.HostStoppingCalls).IsEqualTo(2);
  }

  [Test]
  public async Task ANullRequest_IsRefusedAsync() {
    await Assert.That(() => new RecordingInstanceExit().RequestExit(null!)).Throws<ArgumentNullException>();
  }
}
