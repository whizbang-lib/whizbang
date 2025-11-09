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
public class NamespacePattern {
  private readonly string _pattern;
  private readonly Regex _regex;

  /// <summary>
  /// Creates a new namespace pattern.
  /// </summary>
  /// <param name="pattern">Pattern with wildcards (e.g., "MyApp.Orders.*")</param>
  public NamespacePattern(string pattern) {
    _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
    _regex = PatternToRegex(pattern);
  }

  /// <summary>
  /// Checks if a message type matches this pattern.
  /// </summary>
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
  private static Regex PatternToRegex(string pattern) {
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
  public override string ToString() => _pattern;
}
