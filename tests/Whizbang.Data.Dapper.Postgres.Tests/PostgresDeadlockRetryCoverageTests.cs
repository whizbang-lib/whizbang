// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// A <c>maxAttempts</c> below one asks for no attempt at all, which is a caller's configuration
/// error rather than a request. It is rejected before the action is touched: running it anyway
/// would ignore the setting, and returning without running it (the defect in #1185) told the
/// caller a write had succeeded when it never executed.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresDeadlockRetry.cs</code-under-test>
public class PostgresDeadlockRetryCoverageTests {
  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task ExecuteAsync_WithNonPositiveMaxAttempts_ThrowsWithoutRunningTheActionAsync(int maxAttempts) {
    var ran = false;

    await Assert.That(() => PostgresDeadlockRetry.ExecuteAsync(
        () => {
          ran = true;
          return Task.CompletedTask;
        }, maxAttempts))
      .ThrowsExactly<ArgumentOutOfRangeException>();
    await Assert.That(ran).IsFalse()
      .Because("an action that never ran must not be reported as a success, and must not run at all");
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task ExecuteAsyncOfT_WithNonPositiveMaxAttempts_ThrowsWithoutRunningTheActionAsync(int maxAttempts) {
    var ran = false;

    await Assert.That(() => PostgresDeadlockRetry.ExecuteAsync(
        () => {
          ran = true;
          return Task.FromResult(1);
        }, maxAttempts))
      .ThrowsExactly<ArgumentOutOfRangeException>();
    await Assert.That(ran).IsFalse();
  }

  /// <summary>One attempt is the smallest valid setting, and it runs the action exactly once.</summary>
  [Test]
  public async Task ExecuteAsync_WithOneMaxAttempt_RunsTheActionOnceAsync() {
    var runs = 0;

    await PostgresDeadlockRetry.ExecuteAsync(() => {
      runs++;
      return Task.CompletedTask;
    }, maxAttempts: 1);

    await Assert.That(runs).IsEqualTo(1);
  }
}
