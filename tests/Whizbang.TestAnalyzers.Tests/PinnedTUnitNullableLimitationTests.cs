// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Exceptions;

namespace Whizbang.TestAnalyzers.Tests;

/// <summary>
/// The TUnit limitation WHIZ603 was written for, and its end (ai-docs/testing-tunit.md, "Pinning a limitation that
/// justified a decision").
/// </summary>
/// <remarks>
/// In TUnit 1.12.125, <c>IsNotNull()</c> on a <c>Nullable&lt;T&gt;</c> mapped the subject through <c>.Value</c>; on a
/// null subject that threw, the mapping recorded the exception and a default <c>T</c>, and the null check read only the
/// default, which is never null. TUnit 1.13.8 made the check fail on the recorded exception (upstream pull request
/// 4685), and the repository now runs a TUnit past it. WHIZ603 stays as a style rule: <c>HasValue</c>, or the expected
/// value itself, says what the test means.
/// </remarks>
public class PinnedTUnitNullableLimitationTests {
  [Test]
  public async Task CurrentTUnit_IsNotNullOnNullNullable_FailsAsync() {
    int? missing = null;

    await Assert.That(async () => {
#pragma warning disable WHIZ603 // The call the rule forbids, made on purpose: this test pins its behavior.
      await Assert.That(missing).IsNotNull();
#pragma warning restore WHIZ603
    }).Throws<AssertionException>()
      .Because("TUnit past 1.13.8 fails IsNotNull() on a null Nullable<T>. IF THIS ASSERTION FAILS, TUnit has "
        + "regressed to letting it pass, and WHIZ603 is again the only thing keeping such assertions out.");
  }

  [Test]
  public async Task HasValueIsTrue_OnNullNullable_FailsAsync() {
    int? missing = null;

    await Assert.That(async () => await Assert.That(missing.HasValue).IsTrue())
      .Throws<AssertionException>()
      .Because("the replacement WHIZ603 asks for has to be able to fail, or the rule only moves the hole");
  }
}
