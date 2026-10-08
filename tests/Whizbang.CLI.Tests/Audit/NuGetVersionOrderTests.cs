// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.CLI.Audit;

namespace Whizbang.CLI.Tests.Audit;

/// <summary>
/// Tests for <see cref="NuGetVersionOrder"/>, which picks the nearest fixed version above the
/// version a project resolves.
/// </summary>
/// <tests>Whizbang.CLI/Audit/NuGetVersionOrder.cs</tests>
public class NuGetVersionOrderTests {

  [Test]
  [Arguments("1.2.3", "1.2.3", 0)]
  [Arguments("1.2.3", "1.2.4", -1)]
  [Arguments("1.10.0", "1.9.0", 1)]
  [Arguments("0.2615.0", "0.2616.0-alpha.30", -1)]
  public async Task Compare_ReleaseParts_OrderNumericallyAsync(string left, string right, int expected) {
    // 1.10 above 1.9 is the case a text comparison gets wrong.
    await Assert.That(Math.Sign(NuGetVersionOrder.Compare(left, right))).IsEqualTo(expected);
  }

  [Test]
  [Arguments("1.0.0-alpha.1", "1.0.0", -1)]
  [Arguments("1.0.0", "1.0.0-rc.1", 1)]
  [Arguments("1.0.0-alpha.2", "1.0.0-alpha.10", -1)]
  [Arguments("1.0.0-alpha.9", "1.0.0-beta.1", -1)]
  [Arguments("1.0.0-alpha", "1.0.0-alpha.1", -1)]
  [Arguments("1.0.0-alpha.1", "1.0.0-alpha", 1)]
  [Arguments("1.0.0-1", "1.0.0-alpha", -1)]
  [Arguments("1.0.0-alpha", "1.0.0-1", 1)]
  [Arguments("1.0.0-ALPHA.1", "1.0.0-alpha.1", 0)]
  public async Task Compare_PreReleaseLabels_FollowSemVerAsync(string left, string right, int expected) {
    // A release outranks its own pre-releases, numeric identifiers compare as numbers and
    // below text ones, and a longer label outranks its own prefix. NuGet ignores case.
    await Assert.That(Math.Sign(NuGetVersionOrder.Compare(left, right))).IsEqualTo(expected);
  }

  [Test]
  public async Task Compare_BuildMetadata_IsIgnoredAsync() {
    await Assert.That(NuGetVersionOrder.Compare("1.2.3+abc", "1.2.3+def")).IsEqualTo(0);
  }

  [Test]
  [Arguments("a1b2c3d", "1.0.0")]
  [Arguments("1.0.0", "a1b2c3d")]
  public async Task Compare_NotAVersion_FallsBackToOrdinalTextAsync(string left, string right) {
    // A git commit in a range is not a version. The order must still be total and stable.
    var expected = Math.Sign(string.CompareOrdinal(left, right));

    await Assert.That(Math.Sign(NuGetVersionOrder.Compare(left, right))).IsEqualTo(expected);
  }
}
