// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Which ordering a perspective write is guarded on, shared by both PostgreSQL drivers (#1819).
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Perspectives/PerspectiveRowVersionCommands.cs</code-under-test>
[Category("Unit")]
public class PerspectiveRowVersionCommandsTests {
  /// <summary>A versioned model orders on the event id outside a rebuild: live processing and a rewind.</summary>
  [Test]
  [Arguments(null)]
  [Arguments(ProcessingMode.Live)]
  [Arguments(ProcessingMode.Replay)]
  public async Task OrdersOnEventId_VersionedModelOutsideARebuild_IsTrueAsync(ProcessingMode? mode) {
    await Assert.That(PerspectiveRowVersionCommands.OrdersOnEventId(isVersionedTarget: true, mode)).IsTrue();
  }

  /// <summary>
  /// A rebuild's write is not ordered on the event id, even for a versioned model: it re-derived the row from every
  /// event and ends on the last one by stream version.
  /// </summary>
  [Test]
  public async Task OrdersOnEventId_VersionedModelInARebuild_IsFalseAsync() {
    await Assert.That(PerspectiveRowVersionCommands.OrdersOnEventId(isVersionedTarget: true, ProcessingMode.Rebuild))
      .IsFalse();
  }

  /// <summary>A model that does not opt in never orders on the event id, in any mode.</summary>
  [Test]
  [Arguments(null)]
  [Arguments(ProcessingMode.Live)]
  [Arguments(ProcessingMode.Replay)]
  [Arguments(ProcessingMode.Rebuild)]
  public async Task OrdersOnEventId_UnversionedModel_IsFalseAsync(ProcessingMode? mode) {
    await Assert.That(PerspectiveRowVersionCommands.OrdersOnEventId(isVersionedTarget: false, mode)).IsFalse();
  }

  /// <summary>The event-id guard compares event ids, at or past the stored one.</summary>
  [Test]
  public async Task OrderingGuard_OnTheEventId_ComparesEventIdsAsync() {
    var guard = PerspectiveRowVersionCommands.OrderingGuard("t", "EXCLUDED.metadata", isVersionedTarget: true);

    await Assert.That(guard).IsEqualTo(
      "t.metadata->>'EventId' IS NULL OR EXCLUDED.metadata->>'EventId' >= t.metadata->>'EventId'");
  }

  /// <summary>The commit-sequence guard, which a rebuild's write takes, has no event-id comparison at all.</summary>
  [Test]
  public async Task OrderingGuard_OnTheCommitSequence_DoesNotCompareEventIdsAsync() {
    var guard = PerspectiveRowVersionCommands.OrderingGuard("t", "EXCLUDED.metadata", isVersionedTarget: false);

    await Assert.That(guard).DoesNotContain("EventId");
    await Assert.That(guard).Contains("CommitSequence");
  }
}
