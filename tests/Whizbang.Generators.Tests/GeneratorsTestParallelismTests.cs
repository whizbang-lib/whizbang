// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Locks the memory budget of this project's test run (#2159): every test runs under one limit, and every compilation
/// shares one reference per assembly file.
/// </summary>
/// <code-under-test>tests/Whizbang.Generators.Tests/GeneratorsTestParallelism.cs</code-under-test>
public class GeneratorsTestParallelismTests {
  [Test]
  public async Task TheAssembly_RunsEveryTestUnderTheGeneratorsLimitAsync() {
    var limiters = typeof(GeneratorsTestParallelism).Assembly
      .GetCustomAttributes<ParallelLimiterAttribute<GeneratorsTestParallelism>>()
      .ToList();

    await Assert.That(limiters).Count().IsEqualTo(1)
      .Because("without the assembly limiter TUnit runs eight tests per processor, and the module outgrew a 16 GB runner");
    await Assert.That(new GeneratorsTestParallelism().Limit).IsEqualTo(8)
      .Because("eight is the measured budget; raising it needs a new measurement, not a guess");
  }

  [Test]
  public async Task FromFile_ReturnsOneReferencePerPath_ForTheWholeRunAsync() {
    var path = typeof(object).Assembly.Location;

    var first = SharedMetadataReferences.FromFile(path);
    var second = SharedMetadataReferences.FromFile(path);

    await Assert.That(ReferenceEquals(first, second)).IsTrue()
      .Because("Roslyn reuses an assembly's decoded metadata and symbols only for the same reference instance");
  }
}
