// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Branch backfill for <see cref="LocalSyncSignaler"/>: built by hand with no logger, the signaler
/// falls back to a null logger so logging a dropped handler exception cannot itself fail.
/// </summary>
public class LocalSyncSignalerBranchCoverageTests {

  private sealed class TestPerspective;

  [Test]
  public async Task NullLogger_HandlerThrows_StillNotifiesTheOtherHandlersAsync() {
    using var signaler = new LocalSyncSignaler(logger: null!);
    var perspectiveType = typeof(TestPerspective);
    var goodRan = false;

    using var badSub = signaler.Subscribe(perspectiveType, _ => throw new InvalidOperationException("boom"));
    using var goodSub = signaler.Subscribe(perspectiveType, _ => goodRan = true);

    signaler.SignalCheckpointUpdated(perspectiveType, Guid.NewGuid(), Guid.NewGuid());

    await Assert.That(goodRan).IsTrue()
      .Because("with no logger supplied, the drop is logged to the null-object fallback and the other handlers still run");
  }
}
