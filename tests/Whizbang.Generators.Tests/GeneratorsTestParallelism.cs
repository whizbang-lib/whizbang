// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core.Interfaces;

namespace Whizbang.Generators.Tests;

/// <summary>
/// How many tests of this project run at once (#2159), applied to every test by the assembly attribute in
/// <c>AssemblyInfo.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each test builds a Roslyn compilation and runs a generator or analyzer over it, so what the tests share is memory,
/// and this is sized to it. TUnit's default is eight tests per processor. On a six-processor build-server slot that
/// ran 48 compilations at once and the module's process tree peaked at 21.8 GB; on a four-processor, 16 GB
/// GitHub-hosted runner the runner was shut down part-way through the module, which failed the unit leg whenever CI
/// fell back to a hosted runner.
/// </para>
/// <para>
/// Measured on a six-processor slot with coverage collected, as CI runs it (all 3,870 tests each time): 21.8 GB and
/// 331 s at the default; 13.0 GB and 216 s at eight; with every compilation sharing its references
/// (<see cref="SharedMetadataReferences"/>), 16.5 GB and 236 s at the default and 5.9 GB and 196 s at eight; with both in
/// place and nothing passed on the command line, 6.1 GB and 105 s. More tests at once is slower, not faster: the
/// compilations compete for memory bandwidth and the collector. Eight leaves a 16 GB runner most of its memory for the
/// rest of the unit leg.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Generators.Tests/GeneratorsTestParallelismTests.cs:TheAssembly_RunsEveryTestUnderTheGeneratorsLimitAsync</tests>
public sealed class GeneratorsTestParallelism : IParallelLimit {
  /// <summary>The most tests of this project that run at the same time in one process.</summary>
  public const int MAX_CONCURRENT_TESTS = 8;

  /// <inheritdoc />
  public int Limit => MAX_CONCURRENT_TESTS;
}
