using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for SerializablePropertyAnalyzer WHIZ060-063.
/// Verifies detection of non-serializable properties on ICommand/IEvent types.
/// Organized by test category for 100% line and branch coverage.
/// </summary>
[Category("Analyzers")]
public class SerializablePropertyAnalyzerTests {
  // ========================================
  // Category 1: Type Detection Tests (WHIZ060-062)
  // ========================================

  /// <summary>
  /// Test 1: Object type property on command reports WHIZ060.
  /// Covers: _isObjectType() true branch
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CommandWithObjectProperty_ReportsWHIZ060Async() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public record CreateOrderCommand(object Payload) : ICommand;
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ060")).Count().IsEqualTo(1);
    await Assert.That(diagnostics.First(d => d.Id == "WHIZ060").Severity).IsEqualTo(DiagnosticSeverity.Error);
  }

  /// <summary>
  /// Test 2: Object type property on event reports WHIZ060.
  /// Covers: _isMessageType() for IEvent
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_EventWithObjectProperty_ReportsWHIZ060Async() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public record OrderCreatedEvent(object Data) : IEvent;
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ060")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Test 3: Nullable object type property reports WHIZ060.
  /// Covers: object? handling via Nullable check
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CommandWithNullableObjectProperty_ReportsWHIZ060Async() {
    // Arrange - Note: object? in records is still just object in IL
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class UpdateCommand : ICommand {
              public object? NullableData { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ060")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Test 4: Dynamic type property reports WHIZ061.
  /// Covers: _isDynamicType() true branch
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_EventWithDynamicProperty_ReportsWHIZ061Async() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class DynamicEvent : IEvent {
              public dynamic Payload { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ061")).Count().IsEqualTo(1);
    await Assert.That(diagnostics.First(d => d.Id == "WHIZ061").Severity).IsEqualTo(DiagnosticSeverity.Error);
  }

  /// <summary>
  /// Test 5: Non-generic IEnumerable interface reports WHIZ062.
  /// Covers: _isNonSerializableInterface() true branch for IEnumerable
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CommandWithNonGenericIEnumerable_ReportsWHIZ062Async() {
    // Arrange
    var source = """
            using System.Collections;
            using Whizbang.Core;

            namespace TestApp;

            public class BatchCommand : ICommand {
              public IEnumerable Items { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ062")).Count().IsEqualTo(1);
    await Assert.That(diagnostics.First(d => d.Id == "WHIZ062").Severity).IsEqualTo(DiagnosticSeverity.Error);
  }

  /// <summary>
  /// Test 6: Non-generic IList interface reports WHIZ062.
  /// Covers: _isNonSerializableInterface() for IList
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CommandWithNonGenericIList_ReportsWHIZ062Async() {
    // Arrange
    var source = """
            using System.Collections;
            using Whizbang.Core;

            namespace TestApp;

            public class ListCommand : ICommand {
              public IList Items { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ062")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Test 7: Custom non-generic interface reports WHIZ062.
  /// Covers: interface that is NOT a generic collection
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CommandWithCustomInterface_ReportsWHIZ062Async() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public interface ICustomPayload { }

            public class CustomCommand : ICommand {
              public ICustomPayload Payload { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ062")).Count().IsEqualTo(1);
  }

  // ========================================
  // Category 2: Valid Types (No Error)
  // ========================================

  /// <summary>
  /// Test 8: Generic IEnumerable<T> is valid (no error).
  /// Covers: _isNonSerializableInterface() false branch (has type arguments)
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CommandWithGenericIEnumerable_NoErrorAsync() {
    // Arrange
    var source = """
            using System.Collections.Generic;
            using Whizbang.Core;

            namespace TestApp;

            public class BatchCommand : ICommand {
              public IEnumerable<string> Items { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  /// <summary>
  /// Test 9: Generic IList<T> is valid (no error).
  /// Covers: generic collection valid path
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CommandWithGenericIList_NoErrorAsync() {
    // Arrange
    var source = """
            using System.Collections.Generic;
            using Whizbang.Core;

            namespace TestApp;

            public class ListCommand : ICommand {
              public IList<int> Numbers { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  /// <summary>
  /// Test 10: List<T> is valid (no error).
  /// Covers: concrete generic type valid path
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CommandWithListOfString_NoErrorAsync() {
    // Arrange
    var source = """
            using System.Collections.Generic;
            using Whizbang.Core;

            namespace TestApp;

            public record StringListCommand(List<string> Names) : ICommand;
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  /// <summary>
  /// Test 11: Concrete custom type is valid (no error).
  /// Covers: valid nested type path
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CommandWithConcreteCustomType_NoErrorAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class Address {
              public string Street { get; set; }
              public string City { get; set; }
            }

            public class ShippingCommand : ICommand {
              public Address ShippingAddress { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  /// <summary>
  /// Test 12: Primitive types are valid (no error).
  /// Covers: _isPrimitiveOrFrameworkType() true branch
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CommandWithPrimitiveProperties_NoErrorAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestApp;

            public class PrimitiveCommand : ICommand {
              public int Count { get; set; }
              public string Name { get; set; }
              public decimal Amount { get; set; }
              public bool IsActive { get; set; }
              public Guid Id { get; set; }
              public DateTime Created { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  // ========================================
  // Category 3: Nested Type Recursion
  // ========================================

  /// <summary>
  /// Test 13: Nested type with object property reports WHIZ063.
  /// Covers: recursion into nested type, WHIZ063 reporting
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NestedTypeWithObjectProperty_ReportsWHIZ063Async() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class OrderItem {
              public object Metadata { get; set; }
            }

            public class CreateOrderCommand : ICommand {
              public OrderItem Item { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ063")).Count().IsEqualTo(1);
    await Assert.That(diagnostics.First(d => d.Id == "WHIZ063").Severity).IsEqualTo(DiagnosticSeverity.Error);
  }

  /// <summary>
  /// Test 14: Deeply nested (3 levels) with object property reports WHIZ063.
  /// Covers: multi-level recursion
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_DeeplyNestedTypeWithObjectProperty_ReportsWHIZ063Async() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class Level3 {
              public object DeepData { get; set; }
            }

            public class Level2 {
              public Level3 Nested { get; set; }
            }

            public class Level1 {
              public Level2 Child { get; set; }
            }

            public class DeepCommand : ICommand {
              public Level1 Root { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ063")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Test 15: List<T> element type is checked for nested violations.
  /// Covers: _getElementType() for generic collections
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ListOfNestedTypeWithObjectProperty_ReportsWHIZ063Async() {
    // Arrange
    var source = """
            using System.Collections.Generic;
            using Whizbang.Core;

            namespace TestApp;

            public class OrderItem {
              public object Metadata { get; set; }
            }

            public class BulkOrderCommand : ICommand {
              public List<OrderItem> Items { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ063")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Test 16: Array element type is checked for nested violations.
  /// Covers: _getElementType() for arrays
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ArrayOfNestedTypeWithObjectProperty_ReportsWHIZ063Async() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class OrderItem {
              public object Metadata { get; set; }
            }

            public class ArrayOrderCommand : ICommand {
              public OrderItem[] Items { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ063")).Count().IsEqualTo(1);
  }

  // ========================================
  // Category 4: Loop Prevention
  // ========================================

  /// <summary>
  /// Test 17: Circular reference (A -> B -> A) doesn't cause infinite loop.
  /// Covers: visited.Add() returns false branch
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CircularReference_NoInfiniteLoopAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class TypeA {
              public TypeB Other { get; set; }
            }

            public class TypeB {
              public TypeA Back { get; set; }
            }

            public class CircularCommand : ICommand {
              public TypeA Root { get; set; }
            }
            """;

    // Act - Should complete without hanging
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - No errors (all properties are concrete types)
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  /// <summary>
  /// Test 18: Self-referencing type doesn't cause infinite loop.
  /// Covers: type references itself
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_SelfReferencingType_NoInfiniteLoopAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class TreeNode {
              public string Name { get; set; }
              public TreeNode Parent { get; set; }
              public TreeNode[] Children { get; set; }
            }

            public class TreeCommand : ICommand {
              public TreeNode Root { get; set; }
            }
            """;

    // Act - Should complete without hanging
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - No errors (TreeNode has valid properties)
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  /// <summary>
  /// Test 19: Diamond dependency (A -> B, A -> C, B -> D, C -> D) checks D once.
  /// Covers: visited HashSet deduplication
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_DiamondDependency_ChecksOnceAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class SharedType {
              public object BadProp { get; set; }
            }

            public class BranchB {
              public SharedType Shared { get; set; }
            }

            public class BranchC {
              public SharedType Shared { get; set; }
            }

            public class DiamondCommand : ICommand {
              public BranchB Left { get; set; }
              public BranchC Right { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - Should only report once for SharedType (not twice)
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ063")).Count().IsEqualTo(1);
  }

  // ========================================
  // Category 5: Message Type Detection
  // ========================================

  /// <summary>
  /// Test 20: Non-message class with object property is ignored.
  /// Covers: _isMessageType() returns false
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NonMessageClassWithObjectProperty_NoErrorAsync() {
    // Arrange
    var source = """
            namespace TestApp;

            public class RegularClass {
              public object Payload { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - No errors (not a message type)
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  /// <summary>
  /// Test 21: [WhizbangSerializable] attribute triggers analysis.
  /// Covers: _isMessageType() for [WhizbangSerializable]
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_WhizbangSerializableWithObjectProperty_ReportsWHIZ060Async() {
    // Arrange
    var source = """
            using Whizbang;

            namespace TestApp;

            [WhizbangSerializable]
            public class SerializableDto {
              public object Data { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ060")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Test 22: Internal message type is ignored.
  /// Covers: accessibility check (internal != public)
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InternalMessageType_IgnoredAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            internal class InternalCommand : ICommand {
              public object Data { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - Internal types are ignored
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  // ========================================
  // Category 6: Property Filtering
  // ========================================

  /// <summary>
  /// Test 23: Static object property is ignored.
  /// Covers: property.IsStatic check
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_StaticObjectProperty_NoErrorAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class StaticCommand : ICommand {
              public static object SharedData { get; set; }
              public string Name { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - Static properties are ignored
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  /// <summary>
  /// Test 24: Private object property is ignored.
  /// Covers: DeclaredAccessibility != Public
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_PrivateObjectProperty_NoErrorAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class PrivateCommand : ICommand {
              private object _secret { get; set; }
              public string Name { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - Private properties are ignored
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  /// <summary>
  /// Test 25: Internal object property is ignored.
  /// Covers: non-public accessibility
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InternalObjectProperty_NoErrorAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class InternalPropCommand : ICommand {
              internal object InternalData { get; set; }
              public string Name { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - Internal properties are ignored
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  // ========================================
  // Category 7: Edge Cases
  // ========================================

  /// <summary>
  /// Test 26: Framework types (string) are not recursed into.
  /// Covers: _isPrimitiveOrFrameworkType() for System types
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_StringProperty_NotRecursedAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class StringCommand : ICommand {
              public string Message { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - String is not recursed into
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  /// <summary>
  /// Test 27: Multiple violations on same type reports all.
  /// Covers: all properties checked, multiple diagnostics
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MultipleNonSerializableProperties_ReportsAllAsync() {
    // Arrange
    var source = """
            using System.Collections;
            using Whizbang.Core;

            namespace TestApp;

            public class MultiViolationCommand : ICommand {
              public object First { get; set; }
              public dynamic Second { get; set; }
              public IEnumerable Third { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - Should report all three violations
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ060")).Count().IsEqualTo(1);
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ061")).Count().IsEqualTo(1);
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ062")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Test 28: Nullable<T> is unwrapped correctly (no false positive).
  /// Covers: Nullable<T> handling in _getElementType()
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NullableIntProperty_NoErrorAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class NullableCommand : ICommand {
              public int? OptionalCount { get; set; }
              public decimal? OptionalAmount { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - Nullable value types are valid
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  /// <summary>
  /// Test 29: Dictionary<K,V> is valid.
  /// Covers: multi-type-argument generics
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_DictionaryProperty_NoErrorAsync() {
    // Arrange
    var source = """
            using System.Collections.Generic;
            using Whizbang.Core;

            namespace TestApp;

            public class DictCommand : ICommand {
              public Dictionary<string, int> Counts { get; set; }
              public IDictionary<string, string> Mappings { get; set; }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - Generic dictionaries are valid
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }

  /// <summary>
  /// Test 30: Command with no properties (empty) has no errors.
  /// Covers: empty properties enumeration
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CommandWithNoProperties_NoErrorAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class EmptyCommand : ICommand { }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    // Assert - No properties, no errors
    await Assert.That(diagnostics.Where(d => d.Id.StartsWith("WHIZ06", StringComparison.Ordinal))).IsEmpty();
  }
}
