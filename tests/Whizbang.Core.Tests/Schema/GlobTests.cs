// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Schema;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Schema;

/// <summary>A configured pin matches <c>table:object</c> with <c>*</c> and <c>?</c> wildcards.</summary>
public class GlobTests {
  [Test]
  [Arguments("wh_per_job:idx_job_legacy", "wh_per_job:idx_job_legacy", true)]
  [Arguments("wh_per_*:idx_*_legacy", "wh_per_job:idx_job_code_legacy", true)]
  [Arguments("wh_per_*:idx_*_legacy", "wh_per_job:idx_job_code", false)]
  [Arguments("*", "anything:at_all", true)]
  [Arguments("wh_per_jo?:*", "wh_per_job:x", true)]
  [Arguments("wh_per_jo?:*", "wh_per_jobs:x", false)]
  [Arguments("**:idx", "t:idx", true)]
  [Arguments("a*b*c", "aXXbYYc", true)]
  [Arguments("a*b*c", "aXXbYY", false)]
  [Arguments("WH_PER_JOB:*", "wh_per_job:x", false)]
  [Arguments("", "", true)]
  [Arguments("", "x", false)]
  public async Task MatchesWildcardsAsync(string pattern, string text, bool expected) {
    await Assert.That(Glob.IsMatch(pattern, text)).IsEqualTo(expected);
  }
}
