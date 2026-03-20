using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for TypeMatcher static class.
/// Ensures type name matching works correctly for all MatchStrictness options and regex patterns.
/// </summary>
public class TypeMatcherTests {
  [Test]
  public async Task Matches_ExactMode_ReturnsTrueForIdenticalStringsAsync() {
    // Arrange
    const string type1 = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts";
    const string type2 = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts";
    var strictness = MatchStrictness.Exact;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Matches_ExactMode_ReturnsFalseForDifferentStringsAsync() {
    // Arrange
    const string type1 = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts";
    const string type2 = "ECommerce.Contracts.Events.ProductUpdatedEvent, ECommerce.Contracts";
    var strictness = MatchStrictness.Exact;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task Matches_IgnoreAssemblyMode_MatchesWithoutAssemblyAsync() {
    // Arrange
    const string type1 = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts";
    const string type2 = "ECommerce.Contracts.Events.ProductCreatedEvent, DifferentAssembly";
    var strictness = MatchStrictness.IgnoreAssembly;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Matches_IgnoreNamespaceMode_MatchesSimpleNamesAsync() {
    // Arrange
    const string type1 = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts";
    const string type2 = "Different.Namespace.ProductCreatedEvent, DifferentAssembly";
    var strictness = MatchStrictness.IgnoreNamespace;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert - Should match because simple name is the same
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Matches_IgnoreVersion_StripsVersionInfoAsync() {
    // Arrange
    const string type1 = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    const string type2 = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts, Version=2.0.0.0, Culture=neutral, PublicKeyToken=abcdef";
    var strictness = MatchStrictness.IgnoreVersion;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert - Should match because we're ignoring version info
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Matches_IgnoreCase_CaseInsensitiveAsync() {
    // Arrange
    const string type1 = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts";
    const string type2 = "ecommerce.contracts.events.productcreatedevent, ecommerce.contracts";
    var strictness = MatchStrictness.IgnoreCase;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Matches_CombinedFlags_IgnoreCaseAndVersionAsync() {
    // Arrange
    const string type1 = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts, Version=1.0.0.0";
    const string type2 = "ecommerce.contracts.events.productcreatedevent, ecommerce.contracts, Version=2.0.0.0";
    var strictness = MatchStrictness.IgnoreCase | MatchStrictness.IgnoreVersion;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert - Should match because we're ignoring both case and version
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Matches_CombinedFlags_SimpleNameCaseInsensitiveAsync() {
    // Arrange
    const string type1 = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts";
    const string type2 = "productcreatedevent";
    var strictness = MatchStrictness.SimpleNameCaseInsensitive;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert - Should match simple name case-insensitively
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Matches_SimpleName_MatchesJustTypeNameAsync() {
    // Arrange
    const string type1 = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts";
    const string type2 = "ProductCreatedEvent";
    var strictness = MatchStrictness.SimpleName;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Matches_WithoutAssembly_MatchesNamespaceAndTypeAsync() {
    // Arrange
    const string type1 = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts";
    const string type2 = "ECommerce.Contracts.Events.ProductCreatedEvent, DifferentAssembly";
    var strictness = MatchStrictness.WithoutAssembly;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Matches_RegexPattern_MatchesWildcardsAsync() {
    // Arrange
    const string typeString = "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts";
    var pattern = new Regex(@".*Product.*Event.*");

    // Act
    var result = TypeMatcher.Matches(typeString, pattern);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Matches_RegexPattern_DoesNotMatchWhenNoMatchAsync() {
    // Arrange
    const string typeString = "ECommerce.Contracts.Commands.CreateOrder, ECommerce.Contracts";
    var pattern = new Regex(@".*Product.*Event.*");

    // Act
    var result = TypeMatcher.Matches(typeString, pattern);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task Matches_ExactMode_CaseSensitiveAsync() {
    // Arrange
    const string type1 = "MyNamespace.MyType";
    const string type2 = "myNamespace.myType";
    var strictness = MatchStrictness.Exact;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert - Should NOT match because exact mode is case-sensitive
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task Matches_SimpleName_ExtractsCorrectSimpleNameAsync() {
    // Arrange
    const string type1 = "Namespace.SubNamespace.TypeName, Assembly";
    const string type2 = "TypeName";
    var strictness = MatchStrictness.SimpleName;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Matches_EmptyStrings_ReturnsTrueAsync() {
    // Arrange
    const string type1 = "";
    const string type2 = "";
    var strictness = MatchStrictness.Exact;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Matches_OneEmptyString_ReturnsFalseAsync() {
    // Arrange
    const string type1 = "MyType";
    const string type2 = "";
    var strictness = MatchStrictness.Exact;

    // Act
    var result = TypeMatcher.Matches(type1, type2, strictness);

    // Assert
    await Assert.That(result).IsFalse();
  }
}
