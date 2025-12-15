using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <see cref="RoslynGuards"/>.
/// Note: RoslynGuards is excluded from code coverage because it contains defensive checks
/// for Roslyn compiler bugs. These tests validate the happy path behaviors.
/// The exception paths (Roslyn returning null) are considered "should never happen" scenarios.
/// </summary>
/// <remarks>
/// NOTE: These tests currently have compilation issues due to ModuleInitializerAttribute conflict
/// between PolySharp and System.Runtime. This is a known issue that needs resolution.
/// Tests are stubs pending fix.
/// </remarks>
public class RoslynGuardsTests {

  [Test]
  public async Task GetClassSymbolOrThrow_WithValidClass_ReturnsSymbolAsync() {
    // Arrange
    var source = @"
      namespace TestNamespace {
        public class TestClass { }
      }
    ";
    var tree = CSharpSyntaxTree.ParseText(source);
    var compilation = CSharpCompilation.Create("Test").AddSyntaxTrees(tree);
    var semanticModel = compilation.GetSemanticModel(tree);
    var classDeclaration = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();

    // Act
    var symbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, default);

    // Assert
    await Assert.That(symbol).IsNotNull();
    await Assert.That(symbol.Name).IsEqualTo("TestClass");
  }

  [Test]
  public async Task GetRecordSymbolOrThrow_WithValidRecord_ReturnsSymbolAsync() {
    // Arrange
    var source = @"
      namespace TestNamespace {
        public record TestRecord(string Name);
      }
    ";
    var tree = CSharpSyntaxTree.ParseText(source);
    var compilation = CSharpCompilation.Create("Test").AddSyntaxTrees(tree);
    var semanticModel = compilation.GetSemanticModel(tree);
    var recordDeclaration = tree.GetRoot().DescendantNodes().OfType<RecordDeclarationSyntax>().First();

    // Act
    var symbol = RoslynGuards.GetRecordSymbolOrThrow(recordDeclaration, semanticModel, default);

    // Assert
    await Assert.That(symbol).IsNotNull();
    await Assert.That(symbol.Name).IsEqualTo("TestRecord");
  }

  [Test]
  public async Task GetTypeSymbolFromNode_WithClassDeclaration_ReturnsSymbolAsync() {
    // Arrange
    var source = @"
      namespace TestNamespace {
        public class TestClass { }
      }
    ";
    var tree = CSharpSyntaxTree.ParseText(source);
    var compilation = CSharpCompilation.Create("Test").AddSyntaxTrees(tree);
    var semanticModel = compilation.GetSemanticModel(tree);
    var classDeclaration = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();

    // Act
    var symbol = RoslynGuards.GetTypeSymbolFromNode(classDeclaration, semanticModel, default);

    // Assert
    await Assert.That(symbol).IsNotNull();
    await Assert.That(symbol.Name).IsEqualTo("TestClass");
  }

  [Test]
  public async Task GetTypeSymbolFromNode_WithRecordDeclaration_ReturnsSymbolAsync() {
    // Arrange
    var source = @"
      namespace TestNamespace {
        public record TestRecord(string Name);
      }
    ";
    var tree = CSharpSyntaxTree.ParseText(source);
    var compilation = CSharpCompilation.Create("Test").AddSyntaxTrees(tree);
    var semanticModel = compilation.GetSemanticModel(tree);
    var recordDeclaration = tree.GetRoot().DescendantNodes().OfType<RecordDeclarationSyntax>().First();

    // Act
    var symbol = RoslynGuards.GetTypeSymbolFromNode(recordDeclaration, semanticModel, default);

    // Assert
    await Assert.That(symbol).IsNotNull();
    await Assert.That(symbol.Name).IsEqualTo("TestRecord");
  }

  [Test]
  public async Task GetContainingClassOrThrow_WithNodeInClass_ReturnsClassAsync() {
    // Arrange
    var source = @"
      namespace TestNamespace {
        public class TestClass {
          public void TestMethod() { }
        }
      }
    ";
    var tree = CSharpSyntaxTree.ParseText(source);
    var methodDeclaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();

    // Act
    var containingClass = RoslynGuards.GetContainingClassOrThrow(methodDeclaration);

    // Assert
    await Assert.That(containingClass).IsNotNull();
    await Assert.That(containingClass.Identifier.Text).IsEqualTo("TestClass");
  }

  [Test]
  public async Task IsNullableOfType_WithNullableGuid_ReturnsTrueAsync() {
    // Arrange
    var source = @"
      using System;
      namespace TestNamespace {
        public class TestClass {
          public Guid? NullableGuid { get; set; }
        }
      }
    ";
    var tree = CSharpSyntaxTree.ParseText(source);
    var compilation = CSharpCompilation.Create("Test")
        .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
        .AddReferences(MetadataReference.CreateFromFile(typeof(Guid).Assembly.Location))
        .AddSyntaxTrees(tree);
    var semanticModel = compilation.GetSemanticModel(tree);
    var propertyDeclaration = tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>().First();
    var typeSymbol = semanticModel.GetTypeInfo(propertyDeclaration.Type).Type;

    // Act
    var result = RoslynGuards.IsNullableOfType(typeSymbol!, "System.Guid");

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsNullableOfType_WithNonNullableGuid_ReturnsFalseAsync() {
    // Arrange
    var source = @"
      using System;
      namespace TestNamespace {
        public class TestClass {
          public Guid NonNullableGuid { get; set; }
        }
      }
    ";
    var tree = CSharpSyntaxTree.ParseText(source);
    var compilation = CSharpCompilation.Create("Test")
        .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
        .AddReferences(MetadataReference.CreateFromFile(typeof(Guid).Assembly.Location))
        .AddSyntaxTrees(tree);
    var semanticModel = compilation.GetSemanticModel(tree);
    var propertyDeclaration = tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>().First();
    var typeSymbol = semanticModel.GetTypeInfo(propertyDeclaration.Type).Type;

    // Act
    var result = RoslynGuards.IsNullableOfType(typeSymbol!, "System.Guid");

    // Assert
    await Assert.That(result).IsFalse();
  }

  // TODO: Add tests for exception paths (requires malformed compilation units or mocking)
  // These are intentionally excluded from coverage as they test Roslyn compiler bugs
}
