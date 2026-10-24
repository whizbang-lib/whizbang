// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Exceptions;

namespace Whizbang.TestAnalyzers.Tests;

/// <summary>
/// The TUnit limitation WHZ0004 exists for, pinned so that the day it lifts is visible
/// (ai-docs/testing-tunit.md, "Pinning a limitation that justified a decision").
/// </summary>
/// <remarks>
/// In TUnit 1.12.125, <c>IsNotNull()</c> on a <c>Nullable&lt;T&gt;</c> maps the subject through
/// <c>.Value</c>; on a null subject that throws, the mapping records the exception and a default
/// <c>T</c>, and the null check reads only the default, which is never null. TUnit
/// 1.13.8 made the check fail on the recorded exception (upstream pull request 4685).
/// </remarks>
public class PinnedTUnitNullableLimitationTests {
  [Test]
  public async Task PinnedTUnit_IsNotNullOnNullNullable_PassesAsync() {
    int? missing = null;

    await Assert.That(async () => {
#pragma warning disable WHZ0004 // The call the rule forbids, made on purpose: this test pins its behavior.
      await Assert.That(missing).IsNotNull();
#pragma warning restore WHZ0004
    }).ThrowsNothing()
      .Because("the pinned TUnit lets IsNotNull() pass on a null Nullable<T>, which is the entire reason "
        + "WHZ0004 (Whizbang.TestAnalyzers) rejects that call in every test project. IF THIS ASSERTION FAILS "
        + "BECAUSE IsNotNull() NOW THROWS, TUnit has been moved past 1.13.8, where the check was fixed, and "
        + "WHZ0004 can be retired or kept as a style rule; decide which, then invert or delete this test.");
  }

  [Test]
  public async Task HasValueIsTrue_OnNullNullable_FailsAsync() {
    int? missing = null;

    await Assert.That(async () => await Assert.That(missing.HasValue).IsTrue())
      .Throws<AssertionException>()
      .Because("the replacement WHZ0004 asks for has to be able to fail, or the rule only moves the hole");
  }
}
