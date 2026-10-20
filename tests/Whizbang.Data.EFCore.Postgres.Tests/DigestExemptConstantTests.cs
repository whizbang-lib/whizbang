// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.SystemEvents;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The coordinator's recomputes bind <see cref="DigestExemptTypes.TypeNames"/>; the SQL folds read the
/// <c>__DIGEST_EXEMPT_TYPES__</c> migration constant. They must name the same types, or the two kinds of fold
/// disagree on every audit bucket (#1492).
/// </summary>
[Category("Unit")]
public class DigestExemptConstantTests {
  [Test]
  public async Task MigrationConstant_NamesTheSameTypesAsTheCoordinatorAsync() {
    var value = MigrationConstants.Tokens["__DIGEST_EXEMPT_TYPES__"];
    var list = value[(value.IndexOf('[', StringComparison.Ordinal) + 1)..value.LastIndexOf(']')];
    var names = list.Split(',').Select(n => n.Trim().Trim('\'')).ToList();

    await Assert.That(names).IsEquivalentTo(DigestExemptTypes.TypeNames);
  }
}
