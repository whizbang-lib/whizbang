using System;

namespace Whizbang.Core.Versioning;

/// <summary>
/// A Semantic Versioning 2.0.0 version, parsed for <b>precedence</b> — the ordering that decides
/// whether one instance's code is newer than the schema another instance applied.
/// </summary>
/// <remarks>
/// <para>
/// Precedence follows the specification exactly, and three of its rules are the ones that matter
/// here. A pre-release version ranks below the release it precedes. Numeric pre-release identifiers
/// compare <em>numerically</em>, so <c>alpha.10</c> outranks <c>alpha.2</c> where a string comparison
/// says the opposite. Build metadata takes no part in precedence at all and is discarded at parse.
/// </para>
/// <para>
/// The pre-release rules are not an edge case for this framework: every version before 1.0 carries a
/// pre-release label, so they are exercised on every deployment.
/// </para>
/// <para>
/// Parsing is deliberately strict and total — an unrecognised string fails rather than being coerced
/// into something orderable, because a wrong ordering is worse than no ordering when the answer
/// decides whether an instance may write to the schema. No reflection, no allocation beyond the
/// substrings themselves, so it is safe under native AOT.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#an-older-instance-never-overwrites-a-newer-one</docs>
/// <tests>tests/Whizbang.Core.Tests/Versioning/SemanticVersionTests.cs</tests>
public readonly struct SemanticVersion : IEquatable<SemanticVersion>, IComparable<SemanticVersion> {
  /// <summary>The major version.</summary>
  public int Major { get; }

  /// <summary>The minor version.</summary>
  public int Minor { get; }

  /// <summary>The patch version.</summary>
  public int Patch { get; }

  /// <summary>
  /// The dot-separated pre-release identifiers, or empty for a release version. A release always
  /// outranks a pre-release of the same core triple.
  /// </summary>
  public string PreRelease { get; }

  private SemanticVersion(int major, int minor, int patch, string preRelease) {
    Major = major;
    Minor = minor;
    Patch = patch;
    PreRelease = preRelease;
  }

  /// <summary>
  /// Attempts to parse <paramref name="value"/>. Returns <see langword="false"/> for anything that
  /// is not a well-formed <c>major.minor.patch</c> triple with optional pre-release and build
  /// metadata; callers must treat that as "unknown", never as "oldest".
  /// </summary>
  public static bool TryParse(string? value, out SemanticVersion version) {
    version = default;
    if (string.IsNullOrWhiteSpace(value)) {
      return false;
    }

    var span = value.AsSpan().Trim();

    // Build metadata never participates in precedence — drop it before anything else.
    var plus = span.IndexOf('+');
    if (plus >= 0) {
      span = span[..plus];
    }

    var preRelease = string.Empty;
    var dash = span.IndexOf('-');
    if (dash >= 0) {
      var pre = span[(dash + 1)..];
      if (pre.IsEmpty) {
        return false;
      }
      preRelease = pre.ToString();
      span = span[..dash];
    }

    var firstDot = span.IndexOf('.');
    if (firstDot < 0) {
      return false;
    }
    var rest = span[(firstDot + 1)..];
    var secondDot = rest.IndexOf('.');
    if (secondDot < 0) {
      return false;
    }

    if (!_tryParseComponent(span[..firstDot], out var major)
        || !_tryParseComponent(rest[..secondDot], out var minor)
        || !_tryParseComponent(rest[(secondDot + 1)..], out var patch)) {
      return false;
    }

    version = new SemanticVersion(major, minor, patch, preRelease);
    return true;
  }

  private static bool _tryParseComponent(ReadOnlySpan<char> span, out int value) {
    value = 0;
    if (span.IsEmpty) {
      return false;
    }
    foreach (var c in span) {
      if (c is < '0' or > '9') {
        return false;
      }
    }
    return int.TryParse(span, out value);
  }

  /// <inheritdoc />
  public int CompareTo(SemanticVersion other) {
    var core = Major.CompareTo(other.Major);
    if (core != 0) {
      return core;
    }
    core = Minor.CompareTo(other.Minor);
    if (core != 0) {
      return core;
    }
    core = Patch.CompareTo(other.Patch);
    if (core != 0) {
      return core;
    }

    // A release outranks any pre-release of the same core triple.
    var mine = PreRelease.Length == 0;
    var theirs = other.PreRelease.Length == 0;
    if (mine || theirs) {
      return mine && theirs ? 0 : mine ? 1 : -1;
    }

    return _comparePreRelease(PreRelease.AsSpan(), other.PreRelease.AsSpan());
  }

  private static int _comparePreRelease(ReadOnlySpan<char> left, ReadOnlySpan<char> right) {
    while (true) {
      if (left.IsEmpty || right.IsEmpty) {
        // A larger set of identifiers outranks a smaller one when every preceding one is equal.
        return left.IsEmpty && right.IsEmpty ? 0 : left.IsEmpty ? -1 : 1;
      }

      var l = _nextIdentifier(ref left);
      var r = _nextIdentifier(ref right);

      var lNumeric = _isNumeric(l);
      var rNumeric = _isNumeric(r);

      int cmp;
      if (lNumeric && rNumeric) {
        // Numerically, so alpha.10 outranks alpha.2 rather than the reverse.
        cmp = _compareNumeric(l, r);
      } else if (lNumeric != rNumeric) {
        // Numeric identifiers always rank below alphanumeric ones.
        cmp = lNumeric ? -1 : 1;
      } else {
        cmp = l.CompareTo(r, StringComparison.Ordinal);
      }

      if (cmp != 0) {
        return cmp < 0 ? -1 : 1;
      }
    }
  }

  private static ReadOnlySpan<char> _nextIdentifier(ref ReadOnlySpan<char> remaining) {
    var dot = remaining.IndexOf('.');
    if (dot < 0) {
      var whole = remaining;
      remaining = default;
      return whole;
    }
    var head = remaining[..dot];
    remaining = remaining[(dot + 1)..];
    return head;
  }

  private static bool _isNumeric(ReadOnlySpan<char> span) {
    if (span.IsEmpty) {
      return false;
    }
    foreach (var c in span) {
      if (c is < '0' or > '9') {
        return false;
      }
    }
    return true;
  }

  private static int _compareNumeric(ReadOnlySpan<char> left, ReadOnlySpan<char> right) {
    // Compare by significant length first so arbitrarily long identifiers order correctly without
    // needing to fit in any fixed-width integer.
    var l = left.TrimStart('0');
    var r = right.TrimStart('0');
    if (l.Length != r.Length) {
      return l.Length < r.Length ? -1 : 1;
    }
    return l.CompareTo(r, StringComparison.Ordinal);
  }

  /// <inheritdoc />
  public bool Equals(SemanticVersion other) => CompareTo(other) == 0;

  /// <inheritdoc />
  public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

  /// <inheritdoc />
  public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

  /// <inheritdoc />
  public override string ToString() =>
    PreRelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";

  /// <summary>Whether <paramref name="left"/> ranks below <paramref name="right"/>.</summary>
  public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

  /// <summary>Whether <paramref name="left"/> ranks above <paramref name="right"/>.</summary>
  public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

  /// <summary>Whether <paramref name="left"/> ranks at or below <paramref name="right"/>.</summary>
  public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

  /// <summary>Whether <paramref name="left"/> ranks at or above <paramref name="right"/>.</summary>
  public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

  /// <summary>Whether the two versions have equal precedence.</summary>
  public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);

  /// <summary>Whether the two versions differ in precedence.</summary>
  public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);
}
