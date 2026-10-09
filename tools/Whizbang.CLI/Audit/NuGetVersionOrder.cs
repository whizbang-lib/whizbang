// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Whizbang.CLI.Audit;

/// <summary>
/// Orders NuGet version strings the way NuGet does for the purpose of choosing an upgrade target:
/// numeric release parts first, a release above any of its pre-releases, pre-release labels
/// compared identifier by identifier, build metadata ignored.
/// </summary>
/// <remarks>
/// The command needs only "which fixed version is the nearest one above the resolved version",
/// so this covers the SemVer 2 shapes NuGet publishes rather than taking a NuGet client dependency.
/// A string that is not a version (a git commit in a range, say) orders by ordinal text, which
/// keeps the comparison total without pretending it is meaningful.
/// </remarks>
internal static class NuGetVersionOrder {
  /// <summary>
  /// Compares two version strings.
  /// </summary>
  /// <param name="left">The first version.</param>
  /// <param name="right">The second version.</param>
  /// <returns>Negative when left is lower, zero when equal, positive when left is higher.</returns>
  public static int Compare(string left, string right) {
    var (leftRelease, leftLabel) = _split(left);
    var (rightRelease, rightLabel) = _split(right);
    if (!Version.TryParse(leftRelease, out var leftVersion) || !Version.TryParse(rightRelease, out var rightVersion)) {
      return string.CompareOrdinal(left, right);
    }

    var byRelease = leftVersion.CompareTo(rightVersion);
    return byRelease != 0 ? byRelease : _compareLabels(leftLabel, rightLabel);
  }

  private static (string Release, string Label) _split(string version) {
    var withoutMetadata = version.Split('+')[0];
    var dash = withoutMetadata.IndexOf('-', StringComparison.Ordinal);
    return dash < 0 ? (withoutMetadata, "") : (withoutMetadata[..dash], withoutMetadata[(dash + 1)..]);
  }

  // An empty label is a release, which outranks every pre-release of the same numbers.
  private static int _compareLabels(string left, string right) {
    if (left.Length == 0 || right.Length == 0) {
      return right.Length.CompareTo(left.Length);
    }

    var leftParts = left.Split('.');
    var rightParts = right.Split('.');
    for (var i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++) {
      var byPart = _compareIdentifiers(leftParts[i], rightParts[i]);
      if (byPart != 0) {
        return byPart;
      }
    }

    return leftParts.Length.CompareTo(rightParts.Length);
  }

  // Numeric identifiers compare as numbers and rank below text identifiers; text ignores case.
  private static int _compareIdentifiers(string left, string right) {
    var leftIsNumber = long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
    var rightIsNumber = long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
    if (leftIsNumber && rightIsNumber) {
      return leftNumber.CompareTo(rightNumber);
    }

    if (leftIsNumber != rightIsNumber) {
      return leftIsNumber ? -1 : 1;
    }

    return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
  }
}
