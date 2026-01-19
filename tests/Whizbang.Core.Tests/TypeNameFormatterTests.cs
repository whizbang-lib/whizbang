using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for TypeNameFormatter utility class.
/// </summary>
[Category("Core")]
public class TypeNameFormatterTests {

  // ========================================
  // FORMAT TESTS
  // ========================================

  [Test]
  public async Task Format_WithValidType_ReturnsTypeNameAndAssemblyAsync() {
    // Arrange
    var type = typeof(TypeNameFormatterTests);

    // Act
    var result = TypeNameFormatter.Format(type);

    // Assert
    await Assert.That(result).Contains("TypeNameFormatterTests");
    await Assert.That(result).Contains("Whizbang.Core.Tests");
    await Assert.That(result).Contains(", ");
  }

  [Test]
  public async Task Format_WithSystemType_ReturnsCorrectFormatAsync() {
    // Arrange
    var type = typeof(string);

    // Act
    var result = TypeNameFormatter.Format(type);

    // Assert
    await Assert.That(result).IsEqualTo("System.String, System.Private.CoreLib");
  }

  [Test]
  public async Task Format_WithNullType_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => TypeNameFormatter.Format(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Format_WithNestedType_IncludesFullNamespaceAsync() {
    // Arrange
    var type = typeof(NestedTestClass);

    // Act
    var result = TypeNameFormatter.Format(type);

    // Assert
    await Assert.That(result).Contains("TypeNameFormatterTests+NestedTestClass");
  }

  [Test]
  public async Task Format_WithGenericType_IncludesGenericArgsAsync() {
    // Arrange
    var type = typeof(List<string>);

    // Act
    var result = TypeNameFormatter.Format(type);

    // Assert
    await Assert.That(result).Contains("System.Collections.Generic.List");
  }

  // ========================================
  // PARSE TESTS
  // ========================================

  [Test]
  public async Task Parse_WithShortForm_ReturnsSameFormatAsync() {
    // Arrange
    var input = "Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests";

    // Act
    var result = TypeNameFormatter.Parse(input);

    // Assert
    await Assert.That(result).IsEqualTo(input);
  }

  [Test]
  public async Task Parse_WithLongForm_ExtractsShortFormAsync() {
    // Arrange
    var input = "Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    // Act
    var result = TypeNameFormatter.Parse(input);

    // Assert
    await Assert.That(result).IsEqualTo("Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests");
  }

  [Test]
  public async Task Parse_WithExtraWhitespace_TrimsProperlyAsync() {
    // Arrange
    var input = "  Whizbang.Core.Tests.TypeNameFormatterTests  ,  Whizbang.Core.Tests  ";

    // Act
    var result = TypeNameFormatter.Parse(input);

    // Assert
    await Assert.That(result).IsEqualTo("Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests");
  }

  [Test]
  public async Task Parse_WithTypeNameOnly_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var input = "Whizbang.Core.Tests.TypeNameFormatterTests";

    // Act & Assert
    await Assert.That(() => TypeNameFormatter.Parse(input))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task Parse_WithNull_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert - null throws ArgumentNullException
    await Assert.That(() => TypeNameFormatter.Parse(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Parse_WithEmptyOrWhitespace_ThrowsArgumentExceptionAsync() {
    // Act & Assert - empty/whitespace throws ArgumentException
    await Assert.That(() => TypeNameFormatter.Parse(""))
      .ThrowsExactly<ArgumentException>();
    await Assert.That(() => TypeNameFormatter.Parse("   "))
      .ThrowsExactly<ArgumentException>();
  }

  // ========================================
  // TRYPARSE TESTS
  // ========================================

  [Test]
  public async Task TryParse_WithValidInput_ReturnsTrueAndParsedResultAsync() {
    // Arrange
    var input = "Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests";

    // Act
    var success = TypeNameFormatter.TryParse(input, out var result);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(result).IsEqualTo(input);
  }

  [Test]
  public async Task TryParse_WithLongForm_ReturnsTrueAndShortFormAsync() {
    // Arrange
    var input = "Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests, Version=1.0.0.0";

    // Act
    var success = TypeNameFormatter.TryParse(input, out var result);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(result).IsEqualTo("Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests");
  }

  [Test]
  public async Task TryParse_WithNull_ReturnsFalseAsync() {
    // Act
    var success = TypeNameFormatter.TryParse(null, out var result);

    // Assert
    await Assert.That(success).IsFalse();
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task TryParse_WithEmptyString_ReturnsFalseAsync() {
    // Act
    var success = TypeNameFormatter.TryParse("", out var result);

    // Assert
    await Assert.That(success).IsFalse();
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task TryParse_WithWhitespaceOnly_ReturnsFalseAsync() {
    // Act
    var success = TypeNameFormatter.TryParse("   ", out var result);

    // Assert
    await Assert.That(success).IsFalse();
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task TryParse_WithTypeNameOnly_ReturnsFalseAsync() {
    // Arrange
    var input = "Whizbang.Core.Tests.TypeNameFormatterTests";

    // Act
    var success = TypeNameFormatter.TryParse(input, out var result);

    // Assert
    await Assert.That(success).IsFalse();
    await Assert.That(result).IsNull();
  }

  // ========================================
  // ROUNDTRIP TESTS
  // ========================================

  [Test]
  public async Task Format_ThenParse_ReturnsEquivalentResultAsync() {
    // Arrange
    var type = typeof(TypeNameFormatterTests);

    // Act
    var formatted = TypeNameFormatter.Format(type);
    var parsed = TypeNameFormatter.Parse(formatted);

    // Assert
    await Assert.That(parsed).IsEqualTo(formatted);
  }

  // Helper nested class for testing
  private sealed class NestedTestClass { }
}
