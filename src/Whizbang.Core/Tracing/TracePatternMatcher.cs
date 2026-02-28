namespace Whizbang.Core.Tracing;

/// <summary>
/// Provides pattern matching for handler and message names in tracing configuration.
/// </summary>
/// <remarks>
/// <para>
/// Supports multiple pattern types:
/// <list type="bullet">
///   <item><c>OrderReceptor</c> - Exact match</item>
///   <item><c>Order*</c> - Prefix wildcard (starts with)</item>
///   <item><c>*Handler</c> - Suffix wildcard (ends with)</item>
///   <item><c>*Payment*</c> - Contains wildcard</item>
///   <item><c>MyApp.Orders.*</c> - Namespace pattern</item>
///   <item><c>*</c> - Match all</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>tracing/configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/TracePatternMatcherTests.cs</tests>
public static class TracePatternMatcher {
  /// <summary>
  /// Checks if a pattern matches a type name.
  /// </summary>
  /// <param name="pattern">The pattern to match (supports wildcards).</param>
  /// <param name="typeName">The type name to check (simple or fully qualified).</param>
  /// <returns><c>true</c> if the pattern matches; otherwise, <c>false</c>.</returns>
  public static bool IsMatch(string pattern, string typeName) {
    if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(typeName)) {
      return false;
    }

    // Single wildcard matches everything
    if (pattern == "*") {
      return true;
    }

    var startsWithWildcard = pattern.StartsWith('*');
    var endsWithWildcard = pattern.EndsWith('*');

    // *Pattern* - Contains
    if (startsWithWildcard && endsWithWildcard && pattern.Length > 2) {
      var substring = pattern[1..^1];
      return typeName.Contains(substring, StringComparison.Ordinal);
    }

    // *Pattern - Ends with
    if (startsWithWildcard) {
      var suffix = pattern[1..];
      return typeName.EndsWith(suffix, StringComparison.Ordinal);
    }

    // Pattern* - Starts with (or namespace pattern)
    if (endsWithWildcard) {
      var prefix = pattern[..^1];
      return typeName.StartsWith(prefix, StringComparison.Ordinal);
    }

    // Exact match
    return string.Equals(pattern, typeName, StringComparison.Ordinal);
  }

  /// <summary>
  /// Attempts to find a matching pattern and return its associated verbosity.
  /// </summary>
  /// <param name="patterns">Dictionary of patterns to verbosity levels.</param>
  /// <param name="typeName">The type name to match.</param>
  /// <param name="verbosity">The matched verbosity level, or <see cref="TraceVerbosity.Off"/> if no match.</param>
  /// <returns><c>true</c> if a matching pattern was found; otherwise, <c>false</c>.</returns>
  /// <remarks>
  /// Exact matches take precedence over wildcard matches.
  /// </remarks>
  public static bool TryGetMatchingVerbosity(
      IReadOnlyDictionary<string, TraceVerbosity> patterns,
      string typeName,
      out TraceVerbosity verbosity) {
    verbosity = TraceVerbosity.Off;

    if (patterns.Count == 0 || string.IsNullOrEmpty(typeName)) {
      return false;
    }

    // First check for exact match (highest priority)
    if (patterns.TryGetValue(typeName, out var exactMatch)) {
      verbosity = exactMatch;
      return true;
    }

    // Then check wildcard patterns
    foreach (var (pattern, patternVerbosity) in patterns) {
      if (pattern.Contains('*') && IsMatch(pattern, typeName)) {
        verbosity = patternVerbosity;
        return true;
      }
    }

    return false;
  }
}
