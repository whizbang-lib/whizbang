using System;
using System.Text.RegularExpressions;

namespace Whizbang.Core.Transports;

/// <summary>
/// Pattern matching for message type namespaces.
/// Supports wildcards (*) for flexible type discovery.
///
/// Examples:
/// - "MyApp.Orders.*" matches MyApp.Orders.OrderCreated, MyApp.Orders.OrderUpdated
/// - "*.Events" matches MyApp.Orders.Events, MyApp.Payments.Events
/// - "MyApp.*.*" matches MyApp.Orders.OrderCreated, MyApp.Payments.PaymentProcessed
/// </summary>
/// <remarks>
/// Creates a new namespace pattern.
/// </remarks>
/// <param name="pattern">Pattern with wildcards (e.g., "MyApp.Orders.*")</param>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_ExactMatch_ShouldMatchAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_WildcardSuffix_ShouldMatchAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_WildcardSuffix_ShouldNotMatchDifferentNamespaceAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_WildcardPrefix_ShouldMatchAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_DoubleWildcard_ShouldMatchAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_DoubleWildcard_ShouldMatchNestedAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_ShouldNotMatchWhenInsufficientSegmentsAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_ShouldHandleNullNamespaceAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_Constructor_WithNullPattern_ShouldThrowAsync</tests>
public class NamespacePattern(string pattern) {
  private readonly string _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
  private readonly Regex _regex = _patternToRegex(pattern);

  /// <summary>
  /// Checks if a message type matches this pattern.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_ExactMatch_ShouldMatchAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_WildcardSuffix_ShouldMatchAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_WildcardSuffix_ShouldNotMatchDifferentNamespaceAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_WildcardPrefix_ShouldMatchAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_DoubleWildcard_ShouldMatchAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_DoubleWildcard_ShouldMatchNestedAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_ShouldNotMatchWhenInsufficientSegmentsAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_ShouldHandleNullNamespaceAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_Matches_WithNullMessageType_ShouldThrowAsync</tests>
  public bool Matches(Type messageType) {
    ArgumentNullException.ThrowIfNull(messageType);

    var fullName = messageType.FullName;
    if (string.IsNullOrEmpty(fullName)) {
      return false;
    }

    return _regex.IsMatch(fullName);
  }

  /// <summary>
  /// Converts a pattern with wildcards to a regex.
  /// </summary>
  private static Regex _patternToRegex(string pattern) {
    // Escape special regex characters except *
    var escaped = Regex.Escape(pattern);

    // Convert escaped \* back to regex wildcard
    // \* in pattern -> .* in regex (match any characters)
    var regexPattern = escaped.Replace("\\*", ".*");

    // Anchor to start and end
    regexPattern = $"^{regexPattern}$";

    return new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
  }

  /// <summary>
  /// Returns the pattern string.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_ToString_ShouldReturnPatternAsync</tests>
  public override string ToString() => _pattern;
}
