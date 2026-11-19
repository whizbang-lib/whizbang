// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core.Interfaces;
using Whizbang.Tests.Shared;

namespace Whizbang.Testing.Tests.TestIsolation;

/// <summary>
/// A keyed <c>[NotInParallel]</c> test runs apart from every test that has no constraint, as it did before TUnit 1.40,
/// in every test project (tests/KeyedNotInParallelRunsApart.cs, compiled into each one by tests/Directory.Build.props).
/// </summary>
/// <remarks>
/// TUnit 1.40 started the keyed bucket beside the unconstrained one (thomhurst/TUnit#5740). The suite's keys name a
/// shared resource only among the tests that write it; the tests that merely read it carry no attribute and relied on
/// never overlapping a keyed writer. Each test here reads the constraints TUnit will schedule it by.
/// </remarks>
public class KeyedNotInParallelRunsApartTests {
  private static IReadOnlyList<IParallelConstraint> _constraints() => TestContext.Current!.Parallelism.Constraints;

  private static IEnumerable<string> _groups() => _constraints().OfType<ParallelGroupConstraint>().Select(group => group.Group);

  [Test]
  [NotInParallel("KeyedNotInParallelRunsApartTests.Method")] // holds a key of its own so the shared group can be seen on it
  public async Task AMethodKey_PutsTheTestInTheKeyedGroupAsync() {
    await Assert.That(_groups()).IsEquivalentTo([KeyedNotInParallelRunsApartAttribute.GROUP]);
  }

  [Test]
  [NotInParallel(["KeyedNotInParallelRunsApartTests.A", "KeyedNotInParallelRunsApartTests.B"])] // holds keys of its own so the shared group can be seen on it
  public async Task SeveralKeys_PutTheTestInTheKeyedGroupOnceAsync() {
    await Assert.That(_groups()).IsEquivalentTo([KeyedNotInParallelRunsApartAttribute.GROUP]);
  }

  [Test]
  public async Task NoConstraint_LeavesTheTestUngroupedAsync() {
    await Assert.That(_constraints()).IsEmpty();
  }

  [Test]
  [NotInParallel] // runs alone already; the check is that it is not moved into the keyed group
  public async Task NoKey_LeavesTheTestToRunAloneAsync() {
    await Assert.That(_groups()).IsEmpty();
    await Assert.That(_constraints().OfType<NotInParallelConstraint>().Single().NotInParallelConstraintKeys).IsEmpty();
  }

  [Test]
  [ParallelGroup("KeyedNotInParallelRunsApartTests.OwnGroup")]
  [NotInParallel("KeyedNotInParallelRunsApartTests.Grouped")] // holds a key inside a group of its own, which already runs apart
  public async Task AnOwnGroup_IsKeptAsTheOnlyGroupAsync() {
    await Assert.That(_groups()).IsEquivalentTo(["KeyedNotInParallelRunsApartTests.OwnGroup"]);
  }
}

/// <summary>A class-level key reaches every test of the class.</summary>
[NotInParallel("KeyedNotInParallelRunsApartClassTests")] // holds a key of its own so the shared group can be seen on it
public class KeyedNotInParallelRunsApartClassTests {
  [Test]
  public async Task AClassKey_PutsTheTestInTheKeyedGroupAsync() {
    var groups = TestContext.Current!.Parallelism.Constraints.OfType<ParallelGroupConstraint>().Select(group => group.Group);

    await Assert.That(groups).IsEquivalentTo([KeyedNotInParallelRunsApartAttribute.GROUP]);
  }
}
