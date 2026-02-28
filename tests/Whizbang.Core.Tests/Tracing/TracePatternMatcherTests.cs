using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for TracePatternMatcher which handles wildcard and namespace pattern matching.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/TracePatternMatcher.cs</code-under-test>
public class TracePatternMatcherTests {
  #region Exact Match Tests

  [Test]
  public async Task IsMatch_ExactMatch_ReturnsTrueAsync() {
    // Arrange
    const string pattern = "OrderReceptor";
    const string typeName = "OrderReceptor";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsMatch_ExactMatch_CaseSensitive_ReturnsFalseAsync() {
    // Arrange
    const string pattern = "OrderReceptor";
    const string typeName = "orderreceptor";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert - Pattern matching is case-sensitive
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsMatch_ExactMatch_DifferentName_ReturnsFalseAsync() {
    // Arrange
    const string pattern = "OrderReceptor";
    const string typeName = "PaymentReceptor";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region Prefix Wildcard Tests (Pattern*)

  [Test]
  public async Task IsMatch_PrefixWildcard_MatchingPrefix_ReturnsTrueAsync() {
    // Arrange
    const string pattern = "Order*";
    const string typeName = "OrderReceptor";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsMatch_PrefixWildcard_MatchesMultipleVariants_ReturnsTrueAsync() {
    // Arrange
    const string pattern = "Payment*";

    // Act & Assert - Matches anything starting with "Payment"
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "PaymentHandler")).IsTrue();
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "PaymentValidator")).IsTrue();
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "PaymentProcessor")).IsTrue();
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "Payment")).IsTrue();
  }

  [Test]
  public async Task IsMatch_PrefixWildcard_NonMatchingPrefix_ReturnsFalseAsync() {
    // Arrange
    const string pattern = "Order*";
    const string typeName = "PaymentReceptor";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region Suffix Wildcard Tests (*Pattern)

  [Test]
  public async Task IsMatch_SuffixWildcard_MatchingSuffix_ReturnsTrueAsync() {
    // Arrange
    const string pattern = "*Handler";
    const string typeName = "PaymentHandler";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsMatch_SuffixWildcard_MatchesMultipleVariants_ReturnsTrueAsync() {
    // Arrange
    const string pattern = "*Receptor";

    // Act & Assert - Matches anything ending with "Receptor"
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "OrderReceptor")).IsTrue();
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "PaymentReceptor")).IsTrue();
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "Receptor")).IsTrue();
  }

  [Test]
  public async Task IsMatch_SuffixWildcard_NonMatchingSuffix_ReturnsFalseAsync() {
    // Arrange
    const string pattern = "*Handler";
    const string typeName = "OrderReceptor";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region Contains Wildcard Tests (*Pattern*)

  [Test]
  public async Task IsMatch_ContainsWildcard_MatchingSubstring_ReturnsTrueAsync() {
    // Arrange
    const string pattern = "*Payment*";
    const string typeName = "OrderPaymentHandler";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsMatch_ContainsWildcard_AtStart_ReturnsTrueAsync() {
    // Arrange
    const string pattern = "*Order*";
    const string typeName = "OrderHandler";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsMatch_ContainsWildcard_AtEnd_ReturnsTrueAsync() {
    // Arrange
    const string pattern = "*Handler*";
    const string typeName = "PaymentHandler";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsMatch_ContainsWildcard_NotContained_ReturnsFalseAsync() {
    // Arrange
    const string pattern = "*Payment*";
    const string typeName = "OrderHandler";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region Namespace Pattern Tests (Namespace.*)

  [Test]
  public async Task IsMatch_NamespacePattern_MatchingNamespace_ReturnsTrueAsync() {
    // Arrange
    const string pattern = "MyApp.Orders.*";
    const string fullTypeName = "MyApp.Orders.OrderReceptor";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, fullTypeName);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsMatch_NamespacePattern_MatchesMultipleTypes_ReturnsTrueAsync() {
    // Arrange
    const string pattern = "MyApp.Handlers.*";

    // Act & Assert
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "MyApp.Handlers.OrderHandler")).IsTrue();
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "MyApp.Handlers.PaymentHandler")).IsTrue();
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "MyApp.Handlers.Validator")).IsTrue();
  }

  [Test]
  public async Task IsMatch_NamespacePattern_DifferentNamespace_ReturnsFalseAsync() {
    // Arrange
    const string pattern = "MyApp.Orders.*";
    const string fullTypeName = "MyApp.Payments.PaymentHandler";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, fullTypeName);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsMatch_NamespacePattern_PartialMatch_ReturnsFalseAsync() {
    // Arrange - Namespace pattern should match prefix exactly
    const string pattern = "MyApp.Orders.*";
    const string fullTypeName = "MyApp.OrdersExtra.Handler";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, fullTypeName);

    // Assert - "Orders" != "OrdersExtra"
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region Match All Tests

  [Test]
  public async Task IsMatch_SingleWildcard_MatchesEverythingAsync() {
    // Arrange
    const string pattern = "*";

    // Act & Assert - Single wildcard matches everything
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "OrderReceptor")).IsTrue();
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "PaymentHandler")).IsTrue();
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "X")).IsTrue();
    await Assert.That(TracePatternMatcher.IsMatch(pattern, "MyApp.Orders.Handler")).IsTrue();
  }

  #endregion

  #region Edge Cases

  [Test]
  public async Task IsMatch_EmptyPattern_ReturnsFalseAsync() {
    // Arrange
    const string pattern = "";
    const string typeName = "OrderReceptor";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsMatch_EmptyTypeName_ReturnsFalseAsync() {
    // Arrange
    const string pattern = "Order*";
    const string typeName = "";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsMatch_NullPattern_ReturnsFalseAsync() {
    // Arrange
    string? pattern = null;
    const string typeName = "OrderReceptor";

    // Act
    var result = TracePatternMatcher.IsMatch(pattern!, typeName);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsMatch_NullTypeName_ReturnsFalseAsync() {
    // Arrange
    const string pattern = "Order*";
    string? typeName = null;

    // Act
    var result = TracePatternMatcher.IsMatch(pattern, typeName!);

    // Assert
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region TryGetMatchingVerbosity Tests

  [Test]
  public async Task TryGetMatchingVerbosity_ExactMatch_ReturnsTrueWithVerbosityAsync() {
    // Arrange
    var patterns = new Dictionary<string, TraceVerbosity> {
      ["OrderReceptor"] = TraceVerbosity.Debug,
      ["PaymentHandler"] = TraceVerbosity.Verbose
    };
    const string typeName = "OrderReceptor";

    // Act
    var result = TracePatternMatcher.TryGetMatchingVerbosity(patterns, typeName, out var verbosity);

    // Assert
    await Assert.That(result).IsTrue();
    await Assert.That(verbosity).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task TryGetMatchingVerbosity_WildcardMatch_ReturnsTrueWithVerbosityAsync() {
    // Arrange
    var patterns = new Dictionary<string, TraceVerbosity> {
      ["Order*"] = TraceVerbosity.Normal,
      ["*Handler"] = TraceVerbosity.Verbose
    };
    const string typeName = "OrderReceptor";

    // Act
    var result = TracePatternMatcher.TryGetMatchingVerbosity(patterns, typeName, out var verbosity);

    // Assert
    await Assert.That(result).IsTrue();
    await Assert.That(verbosity).IsEqualTo(TraceVerbosity.Normal);
  }

  [Test]
  public async Task TryGetMatchingVerbosity_NoMatch_ReturnsFalseAsync() {
    // Arrange
    var patterns = new Dictionary<string, TraceVerbosity> {
      ["Payment*"] = TraceVerbosity.Debug
    };
    const string typeName = "OrderReceptor";

    // Act
    var result = TracePatternMatcher.TryGetMatchingVerbosity(patterns, typeName, out var verbosity);

    // Assert
    await Assert.That(result).IsFalse();
    await Assert.That(verbosity).IsEqualTo(TraceVerbosity.Off);
  }

  [Test]
  public async Task TryGetMatchingVerbosity_ExactMatchTakesPrecedence_OverWildcardAsync() {
    // Arrange - Exact match should win over wildcard
    var patterns = new Dictionary<string, TraceVerbosity> {
      ["Order*"] = TraceVerbosity.Normal,
      ["OrderReceptor"] = TraceVerbosity.Debug
    };
    const string typeName = "OrderReceptor";

    // Act
    var result = TracePatternMatcher.TryGetMatchingVerbosity(patterns, typeName, out var verbosity);

    // Assert - Exact match (Debug) wins over wildcard (Normal)
    await Assert.That(result).IsTrue();
    await Assert.That(verbosity).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task TryGetMatchingVerbosity_EmptyDictionary_ReturnsFalseAsync() {
    // Arrange
    var patterns = new Dictionary<string, TraceVerbosity>();
    const string typeName = "OrderReceptor";

    // Act
    var result = TracePatternMatcher.TryGetMatchingVerbosity(patterns, typeName, out var verbosity);

    // Assert
    await Assert.That(result).IsFalse();
    await Assert.That(verbosity).IsEqualTo(TraceVerbosity.Off);
  }

  #endregion
}
