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
    const string input = "Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests";

    // Act
    var result = TypeNameFormatter.Parse(input);

    // Assert
    await Assert.That(result).IsEqualTo(input);
  }

  [Test]
  public async Task Parse_WithLongForm_ExtractsShortFormAsync() {
    // Arrange
    const string input = "Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    // Act
    var result = TypeNameFormatter.Parse(input);

    // Assert
    await Assert.That(result).IsEqualTo("Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests");
  }

  [Test]
  public async Task Parse_WithExtraWhitespace_TrimsProperlyAsync() {
    // Arrange
    const string input = "  Whizbang.Core.Tests.TypeNameFormatterTests  ,  Whizbang.Core.Tests  ";

    // Act
    var result = TypeNameFormatter.Parse(input);

    // Assert
    await Assert.That(result).IsEqualTo("Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests");
  }

  [Test]
  public async Task Parse_WithTypeNameOnly_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    const string input = "Whizbang.Core.Tests.TypeNameFormatterTests";

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
    const string input = "Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests";

    // Act
    var success = TypeNameFormatter.TryParse(input, out var result);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(result).IsEqualTo(input);
  }

  [Test]
  public async Task TryParse_WithLongForm_ReturnsTrueAndShortFormAsync() {
    // Arrange
    const string input = "Whizbang.Core.Tests.TypeNameFormatterTests, Whizbang.Core.Tests, Version=1.0.0.0";

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
    const string input = "Whizbang.Core.Tests.TypeNameFormatterTests";

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

  // ========================================
  // GETFULLNAME TESTS
  // ========================================

  [Test]
  public async Task GetFullName_WithAssemblyQualified_ExtractsFullNameAsync() {
    var result = TypeNameFormatter.GetFullName("MyApp.Events.OrderCreated, MyApp");
    await Assert.That(result).IsEqualTo("MyApp.Events.OrderCreated");
  }

  [Test]
  public async Task GetFullName_WithGlobalPrefix_StripsGlobalAsync() {
    var result = TypeNameFormatter.GetFullName("global::MyApp.Events.OrderCreated");
    await Assert.That(result).IsEqualTo("MyApp.Events.OrderCreated");
  }

  [Test]
  public async Task GetFullName_WithSimpleName_ReturnsAsIsAsync() {
    var result = TypeNameFormatter.GetFullName("MyApp.Events.OrderCreated");
    await Assert.That(result).IsEqualTo("MyApp.Events.OrderCreated");
  }

  [Test]
  public async Task GetFullName_WithVersionInfo_StripsAllQualifiersAsync() {
    var result = TypeNameFormatter.GetFullName(
      "MyApp.Events.OrderCreated, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
    await Assert.That(result).IsEqualTo("MyApp.Events.OrderCreated");
  }

  [Test]
  public async Task GetFullName_WithNullOrEmpty_ReturnsInputAsync() {
    await Assert.That(TypeNameFormatter.GetFullName(null!)).IsNull();
    await Assert.That(TypeNameFormatter.GetFullName("")).IsEqualTo("");
  }

  [Test]
  public async Task GetFullName_WithGlobalPrefixAndAssembly_HandlesBothAsync() {
    var result = TypeNameFormatter.GetFullName("global::MyApp.Events.OrderCreated, MyApp");
    await Assert.That(result).IsEqualTo("MyApp.Events.OrderCreated");
  }

  // ========================================
  // GETSIMPLENAME TESTS
  // ========================================

  [Test]
  public async Task GetSimpleName_WithAssemblyQualified_ExtractsSimpleNameAsync() {
    var result = TypeNameFormatter.GetSimpleName("MyApp.Events.OrderCreated, MyApp");
    await Assert.That(result).IsEqualTo("OrderCreated");
  }

  [Test]
  public async Task GetSimpleName_WithGlobalPrefix_ExtractsSimpleNameAsync() {
    var result = TypeNameFormatter.GetSimpleName("global::MyApp.Events.OrderCreated");
    await Assert.That(result).IsEqualTo("OrderCreated");
  }

  [Test]
  public async Task GetSimpleName_WithNestedType_PreservesNestedSeparatorAsync() {
    var result = TypeNameFormatter.GetSimpleName("MyApp.Events.SessionContracts+EndedEvent");
    await Assert.That(result).IsEqualTo("SessionContracts+EndedEvent");
  }

  [Test]
  public async Task GetSimpleName_WithSimpleName_ReturnsAsIsAsync() {
    var result = TypeNameFormatter.GetSimpleName("OrderCreated");
    await Assert.That(result).IsEqualTo("OrderCreated");
  }

  [Test]
  public async Task GetSimpleName_WithNullOrEmpty_ReturnsInputAsync() {
    await Assert.That(TypeNameFormatter.GetSimpleName(null!)).IsNull();
    await Assert.That(TypeNameFormatter.GetSimpleName("")).IsEqualTo("");
  }

  // ========================================
  // GETNAMESPACE TESTS
  // ========================================

  [Test]
  public async Task GetNamespace_WithAssemblyQualified_ExtractsNamespaceAsync() {
    var result = TypeNameFormatter.GetNamespace("MyApp.Events.OrderCreated, MyApp");
    await Assert.That(result).IsEqualTo("MyApp.Events");
  }

  [Test]
  public async Task GetNamespace_WithGlobalPrefix_ExtractsNamespaceAsync() {
    var result = TypeNameFormatter.GetNamespace("global::MyApp.Events.OrderCreated");
    await Assert.That(result).IsEqualTo("MyApp.Events");
  }

  [Test]
  public async Task GetNamespace_WithNoNamespace_ReturnsNullAsync() {
    var result = TypeNameFormatter.GetNamespace("OrderCreated");
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetNamespace_WithNullOrEmpty_ReturnsNullAsync() {
    await Assert.That(TypeNameFormatter.GetNamespace(null!)).IsNull();
    await Assert.That(TypeNameFormatter.GetNamespace("")).IsNull();
  }

  // ========================================
  // GetPayloadNamespace — extracts inner type namespace from generic envelope wrapper
  // ========================================

  [Test]
  public async Task GetPayloadNamespace_GenericEnvelope_ExtractsInnerTypeNamespaceAsync() {
    var result = TypeNameFormatter.GetPayloadNamespace(
      "Whizbang.Core.Observability.MessageEnvelope`1[[JDX.Contracts.Chat.ChatOrchestrationContracts+SwitchedActivityEvent, JDX.Contracts]], Whizbang.Core");
    await Assert.That(result).IsEqualTo("JDX.Contracts.Chat");
  }

  [Test]
  public async Task GetPayloadNamespace_GenericEnvelope_NestedType_ExtractsOuterNamespaceAsync() {
    var result = TypeNameFormatter.GetPayloadNamespace(
      "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Contracts.Chat.Conversations+CreatedEvent, MyApp.Contracts]], Whizbang.Core");
    await Assert.That(result).IsEqualTo("MyApp.Contracts.Chat");
  }

  [Test]
  public async Task GetPayloadNamespace_SimpleType_FallsBackToGetNamespaceAsync() {
    var result = TypeNameFormatter.GetPayloadNamespace(
      "MyApp.Contracts.Chat.MyEvent, MyApp.Contracts");
    await Assert.That(result).IsEqualTo("MyApp.Contracts.Chat");
  }

  [Test]
  public async Task GetPayloadNamespace_NullInput_ReturnsNullAsync() {
    var result = TypeNameFormatter.GetPayloadNamespace(null!);
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetPayloadNamespace_EmptyInput_ReturnsNullAsync() {
    var result = TypeNameFormatter.GetPayloadNamespace("");
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetPayloadNamespace_RealWorldEnvelopeType_ExtractsCorrectlyAsync() {
    // This is the actual format from RabbitMQ transport in JDNext
    var result = TypeNameFormatter.GetPayloadNamespace(
      "Whizbang.Core.Observability.MessageEnvelope`1[[JDX.Contracts.SystemSeeding.SystemSeedContracts+ReseedSystemSucceededEvent, JDX.Contracts]], Whizbang.Core");
    await Assert.That(result).IsEqualTo("JDX.Contracts.SystemSeeding");
  }

  // ==========================================================================
  // GetPerspectiveName tests
  // ==========================================================================

  [Test]
  public async Task GetPerspectiveName_TopLevelType_ReturnsFullNameAsync() {
    var result = TypeNameFormatter.GetPerspectiveName(typeof(TypeNameFormatterTests));
    await Assert.That(result).IsEqualTo(typeof(TypeNameFormatterTests).FullName);
  }

  [Test]
  public async Task GetPerspectiveName_NestedType_UsesPlusSeparatorAsync() {
    var result = TypeNameFormatter.GetPerspectiveName(typeof(NestedTestClass));
    // Should be "Namespace.TypeNameFormatterTests+NestedTestClass" (CLR format)
    await Assert.That(result).Contains("+NestedTestClass");
    await Assert.That(result).IsEqualTo(typeof(NestedTestClass).FullName);
  }

  [Test]
  public async Task GetPerspectiveName_MatchesTypeFullNameAsync() {
    // This is the key consistency guarantee — the method must produce the same
    // output as Type.FullName (which matches BuildClrTypeName from generators)
    var types = new[] { typeof(string), typeof(TypeNameFormatterTests), typeof(NestedTestClass), typeof(DeeplyNested.Inner) };
    foreach (var type in types) {
      var result = TypeNameFormatter.GetPerspectiveName(type);
      await Assert.That(result).IsEqualTo(type.FullName);
    }
  }

  // Helper nested class for testing
  private sealed class NestedTestClass { }

  private sealed class DeeplyNested {
    public sealed class Inner { }
  }
}
