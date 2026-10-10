// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Which candidate lists get the added null test. Only a list holding a null needs it: adding it
/// anywhere else would match rows whose key is absent, which the membership filter does not.
/// </summary>
/// <remarks>
/// The decision is asserted directly because the cases that answer no without looking at a list (a
/// parameter the values do not hold, a list that is itself null, a string) are not shapes a LINQ
/// membership filter produces through the public query path, and the guard is what keeps a future
/// shape from being corrected wrongly.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/QueryTranslation/Containment/ContainmentParameterProcessor.cs</code-under-test>
[Category("Shard1")]
public class ContainmentNullCandidateTests {
  private const string PARAMETER = "__names_0";

  [Test]
  public async Task AListHoldingANull_NeedsTheNullTestAsync() {
    var values = new Dictionary<string, object?> { [PARAMETER] = new List<string?> { "a", null } };

    await Assert.That(ContainmentParameterProcessor.HoldsNullCandidate(values, PARAMETER)).IsTrue();
  }

  [Test]
  public async Task AListWithoutANull_IsLeftAsCompiledAsync() {
    var values = new Dictionary<string, object?> { [PARAMETER] = new[] { "a", "b" } };

    await Assert.That(ContainmentParameterProcessor.HoldsNullCandidate(values, PARAMETER)).IsFalse();
  }

  [Test]
  public async Task AParameterTheValuesDoNotHold_IsLeftAsCompiledAsync() {
    var values = new Dictionary<string, object?> { ["__other_0"] = new List<string?> { null } };

    await Assert.That(ContainmentParameterProcessor.HoldsNullCandidate(values, PARAMETER)).IsFalse();
  }

  [Test]
  public async Task ANullList_IsLeftAsCompiledAsync() {
    var values = new Dictionary<string, object?> { [PARAMETER] = null };

    await Assert.That(ContainmentParameterProcessor.HoldsNullCandidate(values, PARAMETER)).IsFalse();
  }

  [Test]
  public async Task AString_IsOneValueNotAListAsync() {
    var values = new Dictionary<string, object?> { [PARAMETER] = "a" };

    await Assert.That(ContainmentParameterProcessor.HoldsNullCandidate(values, PARAMETER)).IsFalse();
  }
}
