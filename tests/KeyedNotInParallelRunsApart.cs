// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using TUnit.Core;
using TUnit.Core.Interfaces;

[assembly: Whizbang.Tests.Shared.KeyedNotInParallelRunsApart]

namespace Whizbang.Tests.Shared;

/// <summary>
/// Runs every keyed <c>[NotInParallel]</c> test apart from the tests that carry no constraint, as TUnit did before 1.40.
/// Compiled into every test executable under tests/ (tests/Directory.Build.props).
/// </summary>
/// <remarks>
/// <para>
/// Through TUnit 1.39 a session ran in phases: the unconstrained tests, then the parallel groups, then every keyed test
/// (different keys side by side, one key one test at a time), then the keyless <c>[NotInParallel]</c> tests alone. A
/// keyed test therefore never overlapped an unconstrained one. TUnit 1.40 starts the keyed tests beside the
/// unconstrained ones (thomhurst/TUnit#5740), which is what its documentation always said a key means.
/// </para>
/// <para>
/// The suite's keys were written against the phases: a key is shared among the tests that change a process-wide
/// registry, slot or switch, and the tests that only read it carry no attribute. Under 1.40 those readers ran beside the
/// writers and failed in the merge queue, a different test each run. Rather than audit every key for its readers, each
/// keyed test is put in one parallel group. TUnit runs the parallel groups after the unconstrained and keyed tests have
/// finished, and inside a group it still runs different keys side by side and one key one test at a time: the phase the
/// keyed tests had before.
/// </para>
/// <para>
/// The group is added when a test is registered, after every attribute has given the test its constraints. A test
/// with no key (it runs alone, last) or with a parallel group of its own (it runs apart already) is left as it is.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class KeyedNotInParallelRunsApartAttribute : Attribute, ITestRegisteredEventReceiver {
  /// <summary>The parallel group every keyed test without a group of its own joins.</summary>
  internal const string GROUP = "Whizbang.KeyedNotInParallel";

  /// <summary>Last of the registration receivers, so it sees the constraints every other attribute added.</summary>
  public int Order => int.MaxValue;

  /// <inheritdoc />
  public ValueTask OnTestRegistered(TestRegisteredContext context) {
    var parallelism = context.TestContext.Parallelism;
    var keyed = false;
    foreach (var constraint in parallelism.Constraints) {
      if (constraint is ParallelGroupConstraint) {
        return default;
      }
      keyed |= constraint is NotInParallelConstraint { NotInParallelConstraintKeys.Count: > 0 };
    }
    if (keyed) {
      parallelism.AddConstraint(new ParallelGroupConstraint(GROUP, 0));
    }
    return default;
  }
}
