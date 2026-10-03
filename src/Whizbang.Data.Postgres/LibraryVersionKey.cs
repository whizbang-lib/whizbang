using System.Globalization;

namespace Whizbang.Data.Postgres;

/// <summary>
/// A library version as an integer array whose array order is SemVer precedence, so the database can
/// compare two instances' versions with <c>&gt;</c> (role assignment's newest-version preference and
/// cooperative drain, migration 184).
/// </summary>
/// <remarks>
/// <para>
/// The release is padded to three numbers, then a 1 for a release or a 0 for a prerelease, so a
/// release sorts above every prerelease of itself. A prerelease identifier follows as two or more
/// numbers: <c>0, n</c> for a numeric identifier, and <c>1</c>, its characters, then <c>0</c> for an
/// alphanumeric one, so numeric identifiers sort below alphanumeric ones and alphanumeric ones sort
/// by their text, exactly as SemVer orders them. Build metadata is ignored, as SemVer ignores it.
/// </para>
/// <para>
/// A version that cannot be read gives the empty key, which sorts below every version: an instance
/// whose version is unknown never asks another to drain, and is never preferred.
/// </para>
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LibraryVersionKeyTests.cs</tests>
public static class LibraryVersionKey {
  /// <summary>The key for <paramref name="version"/>.</summary>
  /// <param name="version">SemVer text, such as <c>0.2607.0</c> or <c>0.2607.0-alpha.12+abc</c>.</param>
  /// <returns>The key; empty when the version cannot be read.</returns>
  public static int[] From(string? version) {
    if (string.IsNullOrWhiteSpace(version)) {
      return [];
    }
    var text = version.Trim();
    var plus = text.IndexOf('+', StringComparison.Ordinal);
    if (plus >= 0) {
      text = text[..plus];
    }
    var dash = text.IndexOf('-', StringComparison.Ordinal);
    var release = dash >= 0 ? text[..dash] : text;
    var prerelease = dash >= 0 ? text[(dash + 1)..] : null;

    var key = new List<int>();
    foreach (var part in release.Split('.')) {
      if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
        return [];
      }
      key.Add(number);
    }
    while (key.Count < 3) {
      key.Add(0);
    }
    key.Add(prerelease is null ? 1 : 0);
    if (prerelease is not null) {
      foreach (var identifier in prerelease.Split('.')) {
        _appendIdentifier(key, identifier);
      }
    }
    return [.. key];
  }

  private static void _appendIdentifier(List<int> key, string identifier) {
    if (int.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
      key.Add(0);
      key.Add(number);
      return;
    }
    key.Add(1);
    foreach (var c in identifier) {
      key.Add(c);
    }
    key.Add(0);
  }
}
