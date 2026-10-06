// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The SQLSTATE arms of <see cref="TransientDatabaseFailure"/> the primary suite does not name: the
/// remaining admin-shutdown codes, class prefixes reached with codes of other lengths, and near misses
/// that share a prefix or a length with a transient code but are not one.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransientDatabaseFailure.cs</code-under-test>
[Category("Workers")]
public class TransientDatabaseFailureBranchCoverageTests {

  [Test]
  [Arguments("57P02", TransientDatabaseFailure.CONNECTION_LOST)]
  [Arguments("57P03", TransientDatabaseFailure.CONNECTION_LOST)]
  [Arguments("08P01", TransientDatabaseFailure.CONNECTION_LOST)]
  // Five characters ending in "3" but neither lock_not_available nor cannot_connect_now: the
  // classifier must fall past both exact codes to the connection-class prefix.
  [Arguments("08003", TransientDatabaseFailure.CONNECTION_LOST)]
  [Arguments("08", TransientDatabaseFailure.CONNECTION_LOST)]
  [Arguments("08003X", TransientDatabaseFailure.CONNECTION_LOST)]
  [Arguments("53", TransientDatabaseFailure.INSUFFICIENT_RESOURCES)]
  [Arguments("53400", TransientDatabaseFailure.INSUFFICIENT_RESOURCES)]
  [Arguments("53200", TransientDatabaseFailure.INSUFFICIENT_RESOURCES)]
  public async Task ASqlStateThatPasses_ClassifiesByItsReasonAsync(string sqlState, string reason) {
    var found = TransientDatabaseFailure.TryClassify(FakeDbException.WithSqlState(sqlState), out var failure);

    await Assert.That(found).IsTrue();
    await Assert.That(failure!.Reason).IsEqualTo(reason);
    await Assert.That(failure.SqlState).IsEqualTo(sqlState);
  }

  [Test]
  [Arguments("40P02")]
  [Arguments("40002")]
  [Arguments("4000")]
  [Arguments("400010")]
  [Arguments("57015")]
  [Arguments("57P04")]
  [Arguments("57000")]
  [Arguments("55P02")]
  // numeric_value_out_of_range: ends in "3" like the lock-timeout and cannot-connect-now codes, and
  // is a data defect that no retry fixes.
  [Arguments("22003")]
  [Arguments("55000")]
  [Arguments("5")]
  [Arguments("")]
  [Arguments("P0001")]
  [Arguments("XX001")]
  public async Task ANearMiss_IsNotTransientAsync(string sqlState) {
    // A code sharing a class, a prefix or a length with a transient one is still a defect: retrying it
    // would only repeat the failure.
    var found = TransientDatabaseFailure.TryClassify(FakeDbException.WithSqlState(sqlState), out var failure);

    await Assert.That(found).IsFalse();
    await Assert.That(failure).IsNull();
  }

  [Test]
  public async Task NoSqlStateNoInnerCauseAndNotMarkedTransient_IsNotTransientAsync() {
    var found = TransientDatabaseFailure.TryClassify(FakeDbException.WithSqlState(null), out var failure);

    await Assert.That(found).IsFalse()
      .Because("with no code, no wrapped timeout or socket fault and no provider flag, nothing says it will pass");
    await Assert.That(failure).IsNull();
  }
}
