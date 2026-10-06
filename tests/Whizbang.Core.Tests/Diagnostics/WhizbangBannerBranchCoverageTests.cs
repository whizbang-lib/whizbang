// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// Branch coverage for <see cref="WhizbangBanner.Print"/> called with no writer: the banner goes to
/// <see cref="Console.Out"/>, which TUnit captures per test. The colour override is process-wide, so
/// this serialises on the banner tests' key and restores it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Diagnostics/WhizbangBanner.cs</code-under-test>
[NotInParallel("WhizbangBannerEnvironment")]
[Category("Core")]
[Category("Diagnostics")]
public class WhizbangBannerBranchCoverageTests {
  private const string FOOTER_LINK = "https://whizba.ng/";

  [Test]
  public async Task Print_NoWriter_WritesTheBannerToConsoleOutAsync() {
    // Plain mode keeps the output free of escape codes so the footer is matched verbatim. TUnit
    // captures each test's console output, so the banner written to Console.Out is read back from it.
    WhizbangBanner.OutputRedirectedOverride = true;
    try {
      WhizbangBanner.Print();
    } finally {
      WhizbangBanner.OutputRedirectedOverride = null;
    }
    var captured = ((TUnit.Core.Interfaces.ITestOutput)TestContext.Current!).GetStandardOutput();

    await Assert.That(captured).Contains(FOOTER_LINK)
      .Because("with no writer supplied the banner must still reach the console, which is where a host's "
        + "startup banner is read");
  }
}
