// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Whizbang.Core.Capabilities;

/// <summary>Compares the versions a provider reports, to decide whether an upgrade is due.</summary>
/// <docs>operations/infrastructure/capabilities#provisioning</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityVersionTests.cs</tests>
public static class CapabilityVersion {
  /// <summary>
  /// Whether <paramref name="candidate"/> is newer than <paramref name="current"/>: dot-separated segments compared in
  /// order, as numbers when both are numbers and ordinally otherwise, a version with more segments newer than its own
  /// prefix.
  /// </summary>
  /// <param name="candidate">The version that could be installed, or null when unknown.</param>
  /// <param name="current">The installed version, or null when unknown.</param>
  /// <returns>True only when both are known and the candidate is newer.</returns>
  public static bool IsNewer(string? candidate, string? current) {
    if (candidate is null || current is null) {
      return false;
    }
    var offered = candidate.Split('.');
    var installed = current.Split('.');
    for (var i = 0; i < offered.Length; i++) {
      if (i >= installed.Length) {
        return true;
      }
      var compared = _compare(offered[i], installed[i]);
      if (compared != 0) {
        return compared > 0;
      }
    }
    return false;
  }

  private static int _compare(string a, string b) =>
    int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var x)
    && int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
      ? x.CompareTo(y)
      : string.CompareOrdinal(a, b);
}
