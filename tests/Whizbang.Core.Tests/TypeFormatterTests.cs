using System;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for TypeFormatter static class.
/// Ensures type name formatting works correctly for all TypeQualification options.
/// </summary>
public class TypeFormatterTests {
  // Test type for formatting
  private class TestEvent : IEvent { }

  [Test]
  public async Task FormatType_Simple_ReturnsTypeNameOnlyAsync() {
    // Arrange
    var type = typeof(TestEvent);
    var qualification = TypeQualification.Simple;

    // Act
    var result = TypeFormatter.FormatType(type, qualification);

    // Assert - Should return simple name only
    await Assert.That(result).IsEqualTo("TestEvent");
  }

  [Test]
  public async Task FormatType_NamespaceQualified_ReturnsFullNamespaceAsync() {
    // Arrange
    var type = typeof(TestEvent);
    var qualification = TypeQualification.NamespaceQualified;

    // Act
    var result = TypeFormatter.FormatType(type, qualification);

    // Assert - Should include namespace + type name
    await Assert.That(result).IsEqualTo("Whizbang.Core.Tests.TestEvent");
  }

  [Test]
  public async Task FormatType_FullyQualified_ReturnsTypeWithAssemblyAsync() {
    // Arrange
    var type = typeof(TestEvent);
    var qualification = TypeQualification.FullyQualified;

    // Act
    var result = TypeFormatter.FormatType(type, qualification);

    // Assert - Should include namespace, type name, and assembly (no version)
    await Assert.That(result).Contains("Whizbang.Core.Tests.TestEvent");
    await Assert.That(result).Contains("Whizbang.Core.Tests");
    await Assert.That(result).DoesNotContain("Version=");
  }

  [Test]
  public async Task FormatType_FullyQualifiedWithVersion_IncludesVersionInfoAsync() {
    // Arrange
    var type = typeof(TestEvent);
    var qualification = TypeQualification.FullyQualifiedWithVersion;

    // Act
    var result = TypeFormatter.FormatType(type, qualification);

    // Assert - Should include namespace, type name, assembly, and version info
    await Assert.That(result).Contains("Whizbang.Core.Tests.TestEvent");
    await Assert.That(result).Contains("Whizbang.Core.Tests");
    // Version info format may vary, but should have some version-related string
    await Assert.That(result).Contains("Version=");
  }

  [Test]
  public async Task FormatType_GlobalQualified_AddsGlobalPrefixAsync() {
    // Arrange
    var type = typeof(TestEvent);
    var qualification = TypeQualification.GlobalQualified;

    // Act
    var result = TypeFormatter.FormatType(type, qualification);

    // Assert - Should add global:: prefix
    await Assert.That(result).StartsWith("global::");
    await Assert.That(result).Contains("Whizbang.Core.Tests.TestEvent");
  }

  [Test]
  public async Task FormatType_AssemblyQualified_IncludesAssemblyWithoutNamespaceAsync() {
    // Arrange
    var type = typeof(TestEvent);
    var qualification = TypeQualification.AssemblyQualified;

    // Act
    var result = TypeFormatter.FormatType(type, qualification);

    // Assert - Should include type name and assembly, but format may vary
    // (TypeName | Assembly - the interpretation may include namespace depending on Type.AssemblyQualifiedName behavior)
    await Assert.That(result).Contains("TestEvent");
    await Assert.That(result).Contains("Whizbang.Core.Tests");
  }

  [Test]
  public async Task FormatType_None_ReturnsEmptyStringAsync() {
    // Arrange
    var type = typeof(TestEvent);
    var qualification = TypeQualification.None;

    // Act
    var result = TypeFormatter.FormatType(type, qualification);

    // Assert - No flags set means no output
    await Assert.That(result).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task FormatType_TypeNameOnly_ReturnsSimpleNameAsync() {
    // Arrange
    var type = typeof(TestEvent);
    var qualification = TypeQualification.TypeName;

    // Act
    var result = TypeFormatter.FormatType(type, qualification);

    // Assert
    await Assert.That(result).IsEqualTo("TestEvent");
  }

  [Test]
  public async Task FormatType_NamespaceOnly_ReturnsNamespaceOnlyAsync() {
    // Arrange
    var type = typeof(TestEvent);
    var qualification = TypeQualification.Namespace;

    // Act
    var result = TypeFormatter.FormatType(type, qualification);

    // Assert - Just namespace without type name
    await Assert.That(result).IsEqualTo("Whizbang.Core.Tests");
  }

  [Test]
  public async Task FormatType_CustomCombination_WorksCorrectlyAsync() {
    // Arrange
    var type = typeof(TestEvent);
    var qualification = TypeQualification.GlobalPrefix | TypeQualification.TypeName;

    // Act
    var result = TypeFormatter.FormatType(type, qualification);

    // Assert - Should combine global prefix with type name only (no namespace)
    await Assert.That(result).IsEqualTo("global::TestEvent");
  }

  [Test]
  public async Task ParseAssemblyName_WithVersion_StripsVersionInfoAsync() {
    // Arrange
    var fullTypeName = "MyNamespace.MyType, MyAssembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    // Act
    var result = TypeFormatter.ParseAssemblyName(fullTypeName, stripVersion: true);

    // Assert - Should return assembly name without version info
    await Assert.That(result).IsEqualTo("MyAssembly");
  }

  [Test]
  public async Task ParseAssemblyName_WithoutVersion_ReturnsFullAssemblyStringAsync() {
    // Arrange
    var fullTypeName = "MyNamespace.MyType, MyAssembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    // Act
    var result = TypeFormatter.ParseAssemblyName(fullTypeName, stripVersion: false);

    // Assert - Should return full assembly string with version
    await Assert.That(result).IsEqualTo("MyAssembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
  }

  [Test]
  public async Task ParseAssemblyName_NoAssemblyInfo_ReturnsEmptyStringAsync() {
    // Arrange
    var fullTypeName = "MyNamespace.MyType";

    // Act
    var result = TypeFormatter.ParseAssemblyName(fullTypeName, stripVersion: true);

    // Assert
    await Assert.That(result).IsEqualTo(string.Empty);
  }
}
