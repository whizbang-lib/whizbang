// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// How a handler's <c>[CollectiveApplyFor]</c> overrides fold onto the global apply policy. A positive
/// override wins; zero inherits, so a handler that sets nothing applies under the host's policy.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/CollectiveApplyOptions.cs</code-under-test>
public class CollectiveApplyOptionsTests {
  private static readonly CollectiveApplyOptions _global = new() {
    BatchSize = 500,
    StatementTimeoutSeconds = 30,
    SerializeApplies = true,
    LockWaitSeconds = 12,
  };

  private static CollectiveApplyEntry _entry(int batchSizeOverride, int statementTimeoutSecondsOverride) => new(
    ModelType: typeof(object),
    EventType: typeof(object),
    HandlerType: typeof(object),
    MethodName: "Apply",
    ScopeHandling: default,
    SpecKind: default,
    Invoker: static (_, _, _) => new object(),
    BatchSizeOverride: batchSizeOverride,
    StatementTimeoutSecondsOverride: statementTimeoutSecondsOverride);

  [Test]
  public async Task For_WithBothOverrides_TakesTheHandlersValuesAndKeepsTheGlobalKnobsAsync() {
    var effective = _global.For(_entry(batchSizeOverride: 2, statementTimeoutSecondsOverride: 5));

    await Assert.That(effective.BatchSize).IsEqualTo(2);
    await Assert.That(effective.StatementTimeoutSeconds).IsEqualTo(5);
    await Assert.That(effective.SerializeApplies).IsTrue()
      .Because("exclusive serialization is not a per-handler choice");
    await Assert.That(effective.LockWaitSeconds).IsEqualTo(12);
  }

  [Test]
  public async Task For_WithNoOverrides_InheritsTheGlobalPolicyAsync() {
    var effective = _global.For(_entry(batchSizeOverride: 0, statementTimeoutSecondsOverride: 0));

    await Assert.That(effective).IsEqualTo(_global);
  }

  [Test]
  public async Task For_WithANullEntry_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => _global.For(null!)).Throws<ArgumentNullException>();
  }
}
