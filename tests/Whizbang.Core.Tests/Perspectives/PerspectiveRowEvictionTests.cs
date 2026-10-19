// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// <see cref="PerspectiveRowEviction.CanEvict"/> answers whether row retention can evict a model's rows,
/// which is what decides whether the generated runner probes the log on a missing row (issue #1483). Each
/// test registers its own model type, so no registration made elsewhere can change the answer.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention#what-an-eviction-removes</docs>
public class PerspectiveRowEvictionTests {
  private sealed class TtlModel;
  private sealed class CappedModel;
  private sealed class GroupedModel;
  private sealed class UnretainedModel;

  [Test]
  [NotInParallel("PerspectiveTtlRegistryRuntimeConfig")]   // the TTL kill switch other tests flip is process-wide
  public async Task CanEvict_ModelWithARowTtl_IsTrueAsync() {
    PerspectiveTtlRegistry.Register(typeof(TtlModel), 3600);

    await Assert.That(PerspectiveRowEviction.CanEvict(typeof(TtlModel))).IsTrue()
      .Because("the TTL sweep evicts its rows");
  }

  [Test]
  public async Task CanEvict_ModelWithARowCapOnly_IsTrueAsync() {
    PerspectiveRowCapRegistry.Register(typeof(CappedModel), 10, scopeKey: null);

    await Assert.That(PerspectiveRowEviction.CanEvict(typeof(CappedModel))).IsTrue()
      .Because("the cap sweep evicts its rows though it declares no TTL");
  }

  [Test]
  public async Task CanEvict_StreamGroupMemberOnly_IsTrueAsync() {
    PerspectiveStreamGroupRegistry.Register(typeof(GroupedModel), "row-eviction-tests", announce: false, follow: true, bridge: false);

    await Assert.That(PerspectiveRowEviction.CanEvict(typeof(GroupedModel))).IsTrue()
      .Because("the group cascade evicts a member's rows though it declares no retention of its own");
  }

  [Test]
  public async Task CanEvict_ModelWithNoRetentionAndNoGroup_IsFalseAsync() {
    await Assert.That(PerspectiveRowEviction.CanEvict(typeof(UnretainedModel))).IsFalse()
      .Because("nothing evicts its rows, so the runner never pays for the probe");
  }

  [Test]
  public async Task CanEvict_NullModelType_ThrowsAsync() {
    await Assert.That(() => PerspectiveRowEviction.CanEvict(null!)).Throws<ArgumentNullException>();
  }
}
