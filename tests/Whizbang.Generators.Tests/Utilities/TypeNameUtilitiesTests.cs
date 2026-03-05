extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TypeNameUtilities = shared::Whizbang.Generators.Shared.Utilities.TypeNameUtilities;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// Unit tests for TypeNameUtilities.
/// Tests type name extraction and formatting utilities used by all generators.
/// </summary>
public class TypeNameUtilitiesTests {
  #region GetSimpleName(string) Tests

  [Test]
  public async Task GetSimpleName_String_Empty_ReturnsEmptyAsync() {
    // Arrange
    var input = "";

    // Act
    var result = TypeNameUtilities.GetSimpleName(input);

    // Assert
    await Assert.That(result).IsEqualTo("");
  }

  [Test]
  public async Task GetSimpleName_String_SimpleNameNoDot_ReturnsSameAsync() {
    // Arrange - No dot in name
    var input = "Order";

    // Act
    var result = TypeNameUtilities.GetSimpleName(input);

    // Assert
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task GetSimpleName_String_FullyQualified_ReturnsSimpleNameAsync() {
    // Arrange
    var fullyQualified = "global::MyApp.Commands.CreateOrder";

    // Act
    var result = TypeNameUtilities.GetSimpleName(fullyQualified);

    // Assert
    await Assert.That(result).IsEqualTo("CreateOrder");
  }

  [Test]
  public async Task GetSimpleName_String_WithoutNamespace_ReturnsAsIsAsync() {
    // Arrange
    var simpleName = "Order";

    // Act
    var result = TypeNameUtilities.GetSimpleName(simpleName);

    // Assert
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task GetSimpleName_String_ArrayType_HandlesCorrectlyAsync() {
    // Arrange
    var arrayType = "global::MyApp.Events.NotificationEvent[]";

    // Act
    var result = TypeNameUtilities.GetSimpleName(arrayType);

    // Assert
    await Assert.That(result).IsEqualTo("NotificationEvent[]");
  }

  [Test]
  public async Task GetSimpleName_String_TupleType_HandlesCorrectlyAsync() {
    // Arrange
    var tupleType = "(global::MyApp.A, global::MyApp.B)";

    // Act
    var result = TypeNameUtilities.GetSimpleName(tupleType);

    // Assert
    await Assert.That(result).IsEqualTo("(A, B)");
  }

  [Test]
  public async Task GetSimpleName_String_NestedTuple_HandlesCorrectlyAsync() {
    // Arrange - Nested tuple with inner tuple
    var nestedTuple = "(global::A.X, (global::B.Y, global::C.Z))";

    // Act
    var result = TypeNameUtilities.GetSimpleName(nestedTuple);

    // Assert
    await Assert.That(result).IsEqualTo("(X, (Y, Z))");
  }

  [Test]
  public async Task GetSimpleName_String_ArrayInTuple_HandlesCorrectlyAsync() {
    // Arrange
    var arrayInTuple = "(global::A.X[], global::B.Y)";

    // Act
    var result = TypeNameUtilities.GetSimpleName(arrayInTuple);

    // Assert
    await Assert.That(result).IsEqualTo("(X[], Y)");
  }

  #endregion

  #region SplitTupleParts Tests

  [Test]
  public async Task SplitTupleParts_SimpleTuple_SplitsCorrectlyAsync() {
    // Arrange
    var tupleContent = "A, B, C";

    // Act
    var result = TypeNameUtilities.SplitTupleParts(tupleContent);

    // Assert
    await Assert.That(result).Count().IsEqualTo(3);
    await Assert.That(result[0]).IsEqualTo("A");
    await Assert.That(result[1]).IsEqualTo(" B");  // Note: leading space
    await Assert.That(result[2]).IsEqualTo(" C");
  }

  [Test]
  public async Task SplitTupleParts_NestedParentheses_PreservesNestedAsync() {
    // Arrange
    var tupleContent = "A, B, (C, D)";

    // Act
    var result = TypeNameUtilities.SplitTupleParts(tupleContent);

    // Assert
    await Assert.That(result).Count().IsEqualTo(3);
    await Assert.That(result[0]).IsEqualTo("A");
    await Assert.That(result[1]).IsEqualTo(" B");
    await Assert.That(result[2]).IsEqualTo(" (C, D)");
  }

  [Test]
  public async Task SplitTupleParts_Empty_ReturnsEmptyArrayAsync() {
    // Arrange
    var tupleContent = "";

    // Act
    var result = TypeNameUtilities.SplitTupleParts(tupleContent);

    // Assert
    await Assert.That(result).Count().IsEqualTo(0);
  }

  #endregion

  #region GetDbSetPropertyName Tests

  [Test]
  public async Task GetDbSetPropertyName_TopLevel_ReturnsNameWithSAsync() {
    // Arrange - Create a compilation with a top-level class
    var source = @"
namespace TestNamespace {
    public class Order { }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.Order")!;

    // Act
    var result = TypeNameUtilities.GetDbSetPropertyName(typeSymbol);

    // Assert
    await Assert.That(result).IsEqualTo("Orders");
  }

  [Test]
  public async Task GetDbSetPropertyName_Nested_ReturnsParentModelsAsync() {
    // Arrange - Create a compilation with a nested class
    var source = @"
namespace TestNamespace {
    public static class ActiveJobTemplate {
        public class Model { }
    }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.ActiveJobTemplate+Model")!;

    // Act
    var result = TypeNameUtilities.GetDbSetPropertyName(typeSymbol);

    // Assert - Should use containing type name, not "Model"
    await Assert.That(result).IsEqualTo("ActiveJobTemplateModels");
  }

  #endregion

  #region GetTableBaseName Tests

  [Test]
  public async Task GetTableBaseName_TopLevel_ReturnsNameAsync() {
    // Arrange
    var source = @"
namespace TestNamespace {
    public class Order { }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.Order")!;

    // Act
    var result = TypeNameUtilities.GetTableBaseName(typeSymbol);

    // Assert
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task GetTableBaseName_Nested_ReturnsConcatenatedNameAsync() {
    // Arrange
    var source = @"
namespace TestNamespace {
    public static class TaskItem {
        public class Model { }
    }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.TaskItem+Model")!;

    // Act
    var result = TypeNameUtilities.GetTableBaseName(typeSymbol);

    // Assert - Should concatenate parent + nested
    await Assert.That(result).IsEqualTo("TaskItemModel");
  }

  [Test]
  public async Task GetTableBaseName_NestedExactMatch_ReturnsContainingNameOnlyAsync() {
    // Arrange - Nested class has exact same name as containing class
    var source = @"
namespace TestNamespace {
    public static class ActiveAccount {
        public class ActiveAccount { }
    }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.ActiveAccount+ActiveAccount")!;

    // Act
    var result = TypeNameUtilities.GetTableBaseName(typeSymbol);

    // Assert - Should NOT duplicate: just "ActiveAccount" not "ActiveAccountActiveAccount"
    await Assert.That(result).IsEqualTo("ActiveAccount");
  }

  [Test]
  public async Task GetTableBaseName_NestedStartsWithContaining_ReturnsContainingNameOnlyAsync() {
    // Arrange - Nested class name starts with containing class name
    var source = @"
namespace TestNamespace {
    public static class ActiveAccount {
        public class ActiveAccountModel { }
    }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.ActiveAccount+ActiveAccountModel")!;

    // Act
    var result = TypeNameUtilities.GetTableBaseName(typeSymbol);

    // Assert - Should NOT duplicate: just "ActiveAccount" not "ActiveAccountActiveAccountModel"
    await Assert.That(result).IsEqualTo("ActiveAccount");
  }

  [Test]
  public async Task GetTableBaseName_NestedDifferentName_ReturnsConcatenatedAsync() {
    // Arrange - Nested class name does NOT start with containing class name
    var source = @"
namespace TestNamespace {
    public static class OrderViews {
        public class Model { }
    }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.OrderViews+Model")!;

    // Act
    var result = TypeNameUtilities.GetTableBaseName(typeSymbol);

    // Assert - Different names, should concatenate
    await Assert.That(result).IsEqualTo("OrderViewsModel");
  }

  [Test]
  public async Task GetTableBaseName_NestedPartialMatch_ReturnsContainingOnlyAsync() {
    // Arrange - Nested name starts with containing name (partial but valid prefix match)
    var source = @"
namespace TestNamespace {
    public static class Order {
        public class Ordering { }
    }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.Order+Ordering")!;

    // Act
    var result = TypeNameUtilities.GetTableBaseName(typeSymbol);

    // Assert - "Ordering" starts with "Order", so use just "Order"
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task GetTableBaseName_DraftJobPattern_ReturnsContainingOnlyAsync() {
    // Arrange - Real-world pattern: DraftJobCareerStream.DraftJobCareerStream
    var source = @"
namespace TestNamespace {
    public static class DraftJobCareerStream {
        public class DraftJobCareerStream { }
    }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.DraftJobCareerStream+DraftJobCareerStream")!;

    // Act
    var result = TypeNameUtilities.GetTableBaseName(typeSymbol);

    // Assert - Should NOT duplicate
    await Assert.That(result).IsEqualTo("DraftJobCareerStream");
  }

  #endregion

  #region GetSimpleName(INamedTypeSymbol) Tests

  [Test]
  public async Task GetSimpleName_INamedTypeSymbol_TopLevelClass_ReturnsNameAsync() {
    // Arrange
    var source = @"
namespace TestNamespace {
    public class OrderPerspective { }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.OrderPerspective")!;

    // Act
    var result = TypeNameUtilities.GetSimpleName(typeSymbol);

    // Assert
    await Assert.That(result).IsEqualTo("OrderPerspective");
  }

  [Test]
  public async Task GetSimpleName_INamedTypeSymbol_NestedClass_ReturnsParentDotNameAsync() {
    // Arrange
    var source = @"
namespace TestNamespace {
    public static class DraftJobStatus {
        public class Projection { }
    }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.DraftJobStatus+Projection")!;

    // Act
    var result = TypeNameUtilities.GetSimpleName(typeSymbol);

    // Assert - Should include containing type
    await Assert.That(result).IsEqualTo("DraftJobStatus.Projection");
  }

  #endregion

  #region FormatTypeNameForRuntime Tests

  [Test]
  public async Task FormatTypeNameForRuntime_NullTypeSymbol_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    Microsoft.CodeAnalysis.ITypeSymbol? typeSymbol = null;

    // Act & Assert
    await Assert.That(() => TypeNameUtilities.FormatTypeNameForRuntime(typeSymbol!))
        .Throws<System.ArgumentNullException>();
  }

  [Test]
  public async Task FormatTypeNameForRuntime_ReturnsTypeCommaAssemblyAsync() {
    // Arrange
    var source = @"
namespace TestNamespace {
    public class ProductCreatedEvent { }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source, assemblyName: "TestAssembly");
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.ProductCreatedEvent")!;

    // Act
    var result = TypeNameUtilities.FormatTypeNameForRuntime(typeSymbol);

    // Assert - Format should be "TypeName, AssemblyName"
    await Assert.That(result).IsEqualTo("TestNamespace.ProductCreatedEvent, TestAssembly");
  }

  [Test]
  public async Task FormatTypeNameForRuntime_ArrayType_HandlesCorrectlyAsync() {
    // Arrange
    var source = @"
namespace TestNamespace {
    public class OrderEvent { }
    public class Container {
        public OrderEvent[] Events { get; set; }
    }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source, assemblyName: "TestAssembly");
    var containerType = compilation.GetTypeByMetadataName("TestNamespace.Container")!;
    var property = containerType.GetMembers("Events").First() as Microsoft.CodeAnalysis.IPropertySymbol;
    var arrayType = property!.Type;

    // Act
    var result = TypeNameUtilities.FormatTypeNameForRuntime(arrayType);

    // Assert - Should handle array types
    await Assert.That(result).Contains("OrderEvent[]");
    await Assert.That(result).Contains("TestAssembly");
  }

  [Test]
  public async Task FormatTypeNameForRuntime_NestedType_UsesPlusNotDotAsync() {
    // Arrange - Nested class like AuthContracts.TenantCreatedEvent
    var source = @"
namespace TestNamespace {
    public static class AuthContracts {
        public class TenantCreatedEvent { }
    }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source, assemblyName: "TestAssembly");
    // Use CLR metadata name format with '+' to get the nested type
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.AuthContracts+TenantCreatedEvent")!;

    // Act
    var result = TypeNameUtilities.FormatTypeNameForRuntime(typeSymbol);

    // Assert - MUST use '+' for nested types, NOT '.' (CLR format)
    await Assert.That(result).IsEqualTo("TestNamespace.AuthContracts+TenantCreatedEvent, TestAssembly");
  }

  [Test]
  public async Task FormatTypeNameForRuntime_DeeplyNestedType_UsesPlusForAllLevelsAsync() {
    // Arrange - Deeply nested class: Outer.Middle.Inner
    var source = @"
namespace TestNamespace {
    public static class Outer {
        public static class Middle {
            public class Inner { }
        }
    }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source, assemblyName: "TestAssembly");
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.Outer+Middle+Inner")!;

    // Act
    var result = TypeNameUtilities.FormatTypeNameForRuntime(typeSymbol);

    // Assert - All nested levels use '+'
    await Assert.That(result).IsEqualTo("TestNamespace.Outer+Middle+Inner, TestAssembly");
  }

  #endregion

  #region BuildClrTypeName Tests

  [Test]
  public async Task BuildClrTypeName_TopLevelClass_ReturnsNamespaceAndNameAsync() {
    // Arrange
    var source = @"
namespace TestNamespace {
    public class SimpleEvent { }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.SimpleEvent")!;

    // Act
    var result = TypeNameUtilities.BuildClrTypeName(typeSymbol);

    // Assert
    await Assert.That(result).IsEqualTo("TestNamespace.SimpleEvent");
  }

  [Test]
  public async Task BuildClrTypeName_NestedClass_UsesPlusSeparatorAsync() {
    // Arrange
    var source = @"
namespace TestNamespace {
    public static class Container {
        public class NestedEvent { }
    }
}";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestNamespace.Container+NestedEvent")!;

    // Act
    var result = TypeNameUtilities.BuildClrTypeName(typeSymbol);

    // Assert - Uses '+' not '.'
    await Assert.That(result).IsEqualTo("TestNamespace.Container+NestedEvent");
  }

  [Test]
  public async Task BuildClrTypeName_GlobalNamespace_ReturnsTypeNameOnlyAsync() {
    // Arrange - Type in global namespace
    var source = @"
public class GlobalEvent { }
";
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("GlobalEvent")!;

    // Act
    var result = TypeNameUtilities.BuildClrTypeName(typeSymbol);

    // Assert
    await Assert.That(result).IsEqualTo("GlobalEvent");
  }

  #endregion
}
