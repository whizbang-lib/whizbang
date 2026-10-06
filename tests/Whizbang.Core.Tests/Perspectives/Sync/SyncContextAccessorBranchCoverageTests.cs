// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Branch backfill for <see cref="SyncContextAccessor"/>: both getters in an async flow that never
/// had a context holder installed read as no context.
/// </summary>
public class SyncContextAccessorBranchCoverageTests {

  private sealed class TestPerspective;

  [Test]
  public async Task Getters_InAFlowThatNeverSetAContext_ReadNullAsync() {
    // Set a context in THIS flow, then read from a flow that does not inherit it: suppressing
    // execution-context flow gives the work item an empty context, so no holder exists there.
    SyncContextAccessor.CurrentContext = new SyncContext {
      StreamId = Guid.NewGuid(),
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.Synced,
      EventsAwaited = 1,
      ElapsedTime = TimeSpan.FromMilliseconds(100)
    };

    Task<(SyncContext? Static, SyncContext? Instance)> isolated;
    using (ExecutionContext.SuppressFlow()) {
      isolated = Task.Run<(SyncContext? Static, SyncContext? Instance)>(() => (SyncContextAccessor.CurrentContext, new SyncContextAccessor().Current));
    }
    var (staticRead, instanceRead) = await isolated;

    await Assert.That(SyncContextAccessor.CurrentContext).IsNotNull()
      .Because("precondition: this flow does hold a context, so a null below comes from the missing holder");
    await Assert.That(staticRead).IsNull()
      .Because("a flow with no holder installed has no ambient context");
    await Assert.That(instanceRead).IsNull()
      .Because("the scoped accessor reads the same ambient slot and must also report no context");
  }
}
