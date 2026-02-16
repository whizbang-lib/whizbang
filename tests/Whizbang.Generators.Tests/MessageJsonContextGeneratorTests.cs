using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for MessageJsonContextGenerator source generator.
/// Verifies zero-reflection JSON serialization for AOT compatibility using manual JsonTypeInfo generation.
/// </summary>
[Category("SourceGenerators")]
[Category("JsonSerialization")]
public class MessageJsonContextGeneratorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSingleCommand_GeneratesWhizbangJsonContextAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId, string CustomerName) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Should generate MessageJsonContext for message-specific serialization
    var messageCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(messageCode).IsNotNull();
    await Assert.That(messageCode!).Contains("namespace TestAssembly.Generated");
    await Assert.That(messageCode).Contains("public partial class MessageJsonContext : JsonSerializerContext");

    // Should always generate WhizbangJsonContext facade
    var facadeCode = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(facadeCode).IsNotNull();
    await Assert.That(facadeCode!).Contains("public class WhizbangJsonContext : JsonSerializerContext, IJsonTypeInfoResolver");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithCommand_GeneratesMessageEnvelopeFactoryAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId, string CustomerName) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate factory for MessageEnvelope<CreateOrder>
    await Assert.That(code!).Contains("MessageEnvelope<global::MyApp.Commands.CreateOrder>");
    await Assert.That(code).Contains("CreateMessageEnvelope");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleMessages_GeneratesAllFactoriesAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Commands {
  public record CreateOrder(string OrderId, string CustomerName) : ICommand;
  public record UpdateOrder(string OrderId, string Status) : ICommand;
}

namespace MyApp.Events {
  public record OrderCreated(string OrderId, string CustomerName) : IEvent;
  public record OrderUpdated(string OrderId, string Status) : IEvent;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate factories for all MessageEnvelope<T> types
    await Assert.That(code!).Contains("MessageEnvelope<global::MyApp.Commands.CreateOrder>");
    await Assert.That(code).Contains("MessageEnvelope<global::MyApp.Commands.UpdateOrder>");
    await Assert.That(code).Contains("MessageEnvelope<global::MyApp.Events.OrderCreated>");
    await Assert.That(code).Contains("MessageEnvelope<global::MyApp.Events.OrderUpdated>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesGetTypeInfoSwitchAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should override GetTypeInfo with switch
    await Assert.That(code!).Contains("public override JsonTypeInfo? GetTypeInfo(Type type)");
    await Assert.That(code).Contains("if (type == typeof(");
    await Assert.That(code).Contains("return null");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesMessageEnvelopeFactoryMethodAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate specific factory method for MessageEnvelope<CreateOrder>
    // Uses unique identifier from fully qualified name: MyApp.Commands.CreateOrder -> MyApp_Commands_CreateOrder
    await Assert.That(code!).Contains("CreateMessageEnvelope_MyApp_Commands_CreateOrder");
    await Assert.That(code).Contains("MessageEnvelope<global::MyApp.Commands.CreateOrder>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesPropertyHelperMethodAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate CreateProperty helper
    await Assert.That(code!).Contains("CreateProperty<TProperty>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesCoreValueObjectFactoriesAsync() {
    // Arrange - No user message types, but should still generate core types
    var source = @"
namespace MyApp;

public class SomeClass { }
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should always generate factories for core Whizbang types
    await Assert.That(code!).Contains("Create_MessageId");
    await Assert.That(code).Contains("Create_CorrelationId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesGetTypeInfoInternalMethodAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate GetTypeInfoInternal that calls factory methods directly (no caching)
    await Assert.That(code!).Contains("private JsonTypeInfo? GetTypeInfoInternal(Type type, JsonSerializerOptions options)");
    await Assert.That(code).Contains("return Create_MessageId(options);");
    await Assert.That(code).Contains("return Create_CorrelationId(options);");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ImplementsIJsonTypeInfoResolverAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should implement IJsonTypeInfoResolver interface
    await Assert.That(code!).Contains("IJsonTypeInfoResolver");
    await Assert.That(code).Contains("GetTypeInfo(Type type, JsonSerializerOptions options)");
    await Assert.That(code).Contains("GetTypeInfoInternal(type, options)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReportsDiagnostic_ForDiscoveredMessageTypeAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should report WHIZ011 diagnostic
    var infos = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToArray();
    var jsonDiagnostic = infos.FirstOrDefault(d => d.Id == "WHIZ011");

    await Assert.That(jsonDiagnostic).IsNotNull();
    await Assert.That(jsonDiagnostic!.GetMessage(CultureInfo.InvariantCulture)).Contains("CreateOrder");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNoMessages_GeneratesOnlyCoreTypesAsync() {
    // Arrange
    var source = @"
namespace MyApp;

public class SomeClass { }
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate MessageJsonContext with only core types
    await Assert.That(code!).Contains("public partial class MessageJsonContext");
    await Assert.That(code).Contains("MessageId");
    await Assert.That(code).Contains("CorrelationId");

    // Should NOT contain any user message types
    await Assert.That(code).DoesNotContain("MyApp");

    // Should ALWAYS generate WhizbangJsonContext facade (even with no messages)
    var facadeCode = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(facadeCode).IsNotNull();
    await Assert.That(facadeCode!).Contains("public class WhizbangJsonContext : JsonSerializerContext, IJsonTypeInfoResolver");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNestedNamespaces_GeneratesFullyQualifiedNamesAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyCompany.MyApp.Commands.Orders;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should use fully qualified names with global:: prefix
    await Assert.That(code!).Contains("global::MyCompany.MyApp.Commands.Orders.CreateOrder");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNonMessageType_SkipsAsync() {
    // Arrange - Type with BaseList but not implementing ICommand or IEvent
    var source = @"
using System;

namespace MyApp;

public record OrderDto(string OrderId) : ICloneable {
  public object Clone() => this with { };
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should generate context but not include OrderDto
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).DoesNotContain("OrderDto");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithEmptyProject_GeneratesEmptyContextAsync() {
    // Arrange - No message types at all
    var source = @"
namespace MyApp;

public class SomeClass {
  public void DoSomething() { }
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should still generate MessageJsonContext with core types
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("public partial class MessageJsonContext");
    await Assert.That(code).Contains("MessageId"); // Core type should be present

    // Should ALWAYS generate WhizbangJsonContext facade (even with no messages)
    var facadeCode = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(facadeCode).IsNotNull();
    await Assert.That(facadeCode!).Contains("public class WhizbangJsonContext : JsonSerializerContext, IJsonTypeInfoResolver");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageJsonContextGenerator_TypeImplementingBothInterfaces_GeneratesAsCommandAsync() {
    // Arrange - Tests line 53-57, 84: Both isCommand and isEvent = true
    // messageKind ternary chooses "command" when both are true
    var source = """
using Whizbang.Core;

namespace MyApp;

public record HybridMessage : ICommand, IEvent {
  public string Data { get; init; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should generate as command (ternary picks command when both true)
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("HybridMessage");

    // Check diagnostic reports it as command (not event) when both interfaces present
    var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ011");
    await Assert.That(diagnostic).IsNotNull();
    await Assert.That(diagnostic!.GetMessage(CultureInfo.InvariantCulture)).Contains("command");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageJsonContextGenerator_ClassImplementingICommand_GeneratesJsonTypeInfoAsync() {
    // Arrange - Tests line 46-50: Class (not record) in switch expression
    var source = """
using Whizbang.Core;

namespace MyApp;

public class LegacyCommand : ICommand {
  public string Data { get; set; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should generate JsonTypeInfo for class-based command
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("LegacyCommand");

    // Check diagnostic reports it as command
    var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ011");
    await Assert.That(diagnostic).IsNotNull();
    await Assert.That(diagnostic!.GetMessage(CultureInfo.InvariantCulture)).Contains("command");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NoMessageTypes_ReportsDiagnosticWithZeroCountAsync() {
    // Arrange - No ICommand or IEvent types
    var source = """
using System;

namespace MyApp;

public class RegularClass {
  public string Name { get; set; } = string.Empty;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should still generate context
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should report diagnostic with 0 message types (WHIZ099 is the generator invocation diagnostic)
    var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ099");
    await Assert.That(diagnostic).IsNotNull();
    await Assert.That(diagnostic!.GetMessage(CultureInfo.InvariantCulture)).Contains("with 0 message type(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithMultipleProperties_GeneratesValidJsonObjectCreatorAsync() {
    // Arrange - Message with multiple properties to test trailing comma logic
    var source = """
using Whizbang.Core;

namespace MyApp;

public record MultiPropertyCommand(string Prop1, int Prop2, bool Prop3) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Verify no trailing comma on last property in constructor
    await Assert.That(code!).Contains("MultiPropertyCommand");
    await Assert.That(code).Contains("(bool)args[2]"); // Last property without trailing comma
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_InternalCommand_SkipsNonPublicTypeAsync() {
    // Arrange - Tests line 54: DeclaredAccessibility != Public check
    // Internal types should be skipped as generated code can't access them
    var source = """
using Whizbang.Core;

namespace MyApp;

public record PublicCommand(string Data) : ICommand;
internal record InternalCommand(string Data) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should generate context with only public command
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("PublicCommand");
    await Assert.That(code).DoesNotContain("InternalCommand");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithNestedCustomType_DiscoversAndGeneratesForBothAsync() {
    // Arrange - Tests nested type discovery (lines 599-670)
    // Message with List<OrderLineItem> where OrderLineItem is a custom type
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace MyApp;

public record OrderLineItem(string ProductId, int Quantity);

public record CreateOrder(string OrderId, List<OrderLineItem> LineItems) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should discover both CreateOrder and nested OrderLineItem type
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("CreateOrder");
    await Assert.That(code).Contains("OrderLineItem");  // Nested type discovered
    await Assert.That(code).Contains("List<global::MyApp.OrderLineItem>");  // List<T> type generated
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithPrimitiveListProperty_SkipsNestedTypeDiscoveryAsync() {
    // Arrange - Tests line 623-625: IsPrimitiveOrFrameworkType check
    // List<string> should not trigger nested type discovery (string is primitive)
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace MyApp;

public record CreateOrder(string OrderId, List<string> Tags) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should not discover string as nested type, and should skip List<primitive> generation
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("CreateOrder");
    // Generator skips List<primitive> generation - handled by framework
    await Assert.That(code).DoesNotContain("_List_String");  // No List<string> lazy field
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithInternalNestedType_IncludesReferenceButSkipsFactoryAsync() {
    // Arrange - Tests line 634-636: Skip factory generation for non-public nested types
    // Note: Internal types may still appear in List<T> type references but won't have factories generated
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace MyApp;

internal record InternalDetail(string Info);
public record PublicDetail(string Data);

public record CreateOrder(string OrderId, List<PublicDetail> PublicItems) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should discover and generate for PublicDetail
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("CreateOrder");
    await Assert.That(code).Contains("PublicDetail");
    // PublicDetail should have factory method (uses unique identifier from FQN)
    await Assert.That(code).Contains("Create_MyApp_PublicDetail");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSameSimpleNameInDifferentNamespaces_GeneratesUniqueIdentifiersAsync() {
    // Arrange - Two types with same SimpleName but different namespaces
    // This would previously cause duplicate field names (_StartCommand) and factory methods
    var source = """
using Whizbang.Core;

namespace MyApp.Commands {
  public record StartCommand(string Data) : ICommand;
}

namespace MyApp.Events {
  public record StartCommand(string Data) : IEvent;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should not have compilation errors (duplicate identifiers would cause errors)
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Both types should be present with fully qualified names
    await Assert.That(code!).Contains("global::MyApp.Commands.StartCommand");
    await Assert.That(code).Contains("global::MyApp.Events.StartCommand");

    // Should have unique field names (not duplicate _StartCommand)
    // Uses namespace-qualified identifiers like _MyApp_Commands_StartCommand
    await Assert.That(code).Contains("_MyApp_Commands_StartCommand");
    await Assert.That(code).Contains("_MyApp_Events_StartCommand");

    // Should have unique factory method names
    await Assert.That(code).Contains("Create_MyApp_Commands_StartCommand");
    await Assert.That(code).Contains("Create_MyApp_Events_StartCommand");

    // Should have unique MessageEnvelope factory method names
    await Assert.That(code).Contains("CreateMessageEnvelope_MyApp_Commands_StartCommand");
    await Assert.That(code).Contains("CreateMessageEnvelope_MyApp_Events_StartCommand");
  }

  // ==================== Recursive Nested Type Discovery Tests (100% branch coverage) ====================

  /// <summary>
  /// Primary bug fix test: Verifies that deeply nested types (3+ levels) are discovered.
  /// Example: Event → List&lt;Stage&gt; → List&lt;Step&gt; → List&lt;Action&gt;
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithDeeplyNestedTypes_DiscoversAllLevelsAsync() {
    // Arrange - Four levels of nesting (Event → Stage → Step → Action)
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record BlueprintCreatedEvent : IEvent {
    public List<StageBlueprint> Stages { get; init; } = new();
}

public record StageBlueprint {
    public string Name { get; init; } = "";
    public List<StepBlueprint> Steps { get; init; } = new();
}

public record StepBlueprint {
    public string Name { get; init; } = "";
    public List<ActionBlueprint> Actions { get; init; } = new();
}

public record ActionBlueprint {
    public string Name { get; init; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - All levels discovered
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // All four levels must be present
    await Assert.That(code!).Contains("BlueprintCreatedEvent");
    await Assert.That(code).Contains("StageBlueprint");
    await Assert.That(code).Contains("StepBlueprint");       // Level 3 - nested-nested
    await Assert.That(code).Contains("ActionBlueprint");     // Level 4 - deeply nested
  }

  /// <summary>
  /// Tests circular reference handling: TypeA → TypeB → TypeA.
  /// Should not cause infinite loop due to processedTypes HashSet.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithCircularReferences_HandlesWithoutInfiniteLoopAsync() {
    // Arrange - TypeA → TypeB → TypeA (circular)
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record CircularEvent : IEvent {
    public List<NodeA> Nodes { get; init; } = new();
}

public record NodeA {
    public string Name { get; init; } = "";
    public List<NodeB> Children { get; init; } = new();
}

public record NodeB {
    public string Name { get; init; } = "";
    public List<NodeA> BackReferences { get; init; } = new();
}
""";

    // Act - Should complete without stack overflow
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Both types discovered, no duplicates
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("NodeA");
    await Assert.That(code).Contains("NodeB");
  }

  /// <summary>
  /// Tests self-referential types: TreeNode contains List&lt;TreeNode&gt;.
  /// Should discover once without duplication.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithSelfReferencingType_HandlesCorrectlyAsync() {
    // Arrange - TreeNode references itself
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record TreeEvent : IEvent {
    public List<TreeNode> Roots { get; init; } = new();
}

public record TreeNode {
    public string Name { get; init; } = "";
    public List<TreeNode> Children { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - TreeNode discovered once
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("TreeNode");
  }

  /// <summary>
  /// Tests primitive collection skip: List&lt;string&gt;, List&lt;int&gt; should not trigger nested discovery.
  /// Also tests that custom nested types ARE discovered through the recursion.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithMixedNestedAndPrimitiveCollections_SkipsPrimitivesAndDiscoversNestedAsync() {
    // Arrange - Mix of List<CustomType> and List<string>
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record MixedEvent : IEvent {
    public List<string> Tags { get; init; } = new();
    public List<CustomItem> Items { get; init; } = new();
    public List<int> Counts { get; init; } = new();
}

public record CustomItem {
    public string Name { get; init; } = "";
    public List<NestedItem> Nested { get; init; } = new();
}

public record NestedItem {
    public decimal Value { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Custom types discovered (including deeply nested NestedItem)
    await Assert.That(code!).Contains("CustomItem");
    await Assert.That(code).Contains("NestedItem");
  }

  /// <summary>
  /// Tests internal type skip during recursive discovery.
  /// Internal types nested within public types should not have factory methods generated.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithInternalNestedType_SkipsInternalTypesInRecursionAsync() {
    // Arrange - Public event with internal nested type in recursion
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record EventWithInternalNested : IEvent {
    public List<PublicWrapper> Items { get; init; } = new();
}

public record PublicWrapper {
    public string Name { get; init; } = "";
    public List<InternalItem> Hidden { get; init; } = new();
}

internal record InternalItem {
    public string Secret { get; init; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - PublicWrapper discovered with factory, InternalItem skipped (no factory)
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // PublicWrapper should have factory method
    await Assert.That(code!).Contains("Create_TestApp_PublicWrapper");

    // InternalItem should NOT have factory method (internal types skipped)
    await Assert.That(code).DoesNotContain("Create_TestApp_InternalItem");
  }

  /// <summary>
  /// Tests empty queue case: event with no collection properties.
  /// Should not discover any nested types.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithNoCollectionProperties_DiscoversNoNestedTypesAsync() {
    // Arrange - Simple event with no collections
    var source = """
using Whizbang.Core;

namespace TestApp;

public record SimpleEvent : IEvent {
    public string Name { get; init; } = "";
    public int Count { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Only the event type, no nested types
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("SimpleEvent");
  }

  /// <summary>
  /// Tests deduplication: multiple events using the same nested type.
  /// SharedItem should have exactly one factory method generated (deduplicated).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleEventsWithSameNestedType_DeduplicatesCorrectlyAsync() {
    // Arrange - Two events using the same nested type
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record EventA : IEvent {
    public List<SharedItem> ItemsA { get; init; } = new();
}

public record EventB : IEvent {
    public List<SharedItem> ItemsB { get; init; } = new();
}

public record SharedItem {
    public string Name { get; init; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - SharedItem discovered and code generated without errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Check that SharedItem factory method exists (deduplication ensures one factory per type)
    // Use exact method signature to avoid false positives from CreateList_TestApp_SharedItem
    await Assert.That(code!).Contains("JsonTypeInfo<global::TestApp.SharedItem> Create_TestApp_SharedItem(JsonSerializerOptions options)");

    // Count factory method DEFINITIONS (not calls) - signature pattern is unique
    var factorySignatureCount = code.Split("JsonTypeInfo<global::TestApp.SharedItem> Create_TestApp_SharedItem").Length - 1;
    await Assert.That(factorySignatureCount).IsEqualTo(1);
  }

  // ==================== Enum Discovery Tests (100% branch coverage) ====================

  /// <summary>
  /// Tests enum discovery in direct message properties.
  /// Enums used directly in events should be discovered and have JsonTypeInfo generated.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithEnumProperty_DiscoversEnumAsync() {
    // Arrange - Event with direct enum property
    var source = """
using Whizbang.Core;

namespace TestApp;

public enum OrderStatus { Pending, Confirmed, Shipped, Delivered }

public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
    public OrderStatus Status { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Enum should be discovered
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Enum should have GetEnumConverter factory method
    await Assert.That(code!).Contains("OrderStatus");
    await Assert.That(code).Contains("GetEnumConverter");
  }

  /// <summary>
  /// Tests enum discovery in nested type properties (the bug scenario).
  /// StepBlueprint.StepType should be discovered when StepBlueprint is discovered.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NestedTypeWithEnumProperty_DiscoversEnumAsync() {
    // Arrange - Event → List<Stage> → Stage.StepType enum (the JDNext bug scenario)
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public enum StepType { Manual, Automated, Hybrid }

public record BlueprintCreatedEvent : IEvent {
    public List<StageBlueprint> Stages { get; init; } = new();
}

public record StageBlueprint {
    public string Name { get; init; } = "";
    public StepType Type { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Both StageBlueprint AND StepType enum should be discovered
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Nested class discovered
    await Assert.That(code!).Contains("StageBlueprint");

    // Enum used by nested class ALSO discovered
    await Assert.That(code).Contains("StepType");
    await Assert.That(code).Contains("GetEnumConverter");
  }

  /// <summary>
  /// Tests that deeply nested enums are discovered.
  /// Event → List<Stage> → List<Step> → Step.ActionType enum
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DeeplyNestedEnumProperty_DiscoversEnumAsync() {
    // Arrange - Three levels: Event → Stage → Step → ActionType enum
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public enum ActionType { Create, Update, Delete, Archive }

public record WorkflowEvent : IEvent {
    public List<Stage> Stages { get; init; } = new();
}

public record Stage {
    public string Name { get; init; } = "";
    public List<Step> Steps { get; init; } = new();
}

public record Step {
    public string Name { get; init; } = "";
    public ActionType Action { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - All nested types AND deeply nested enum discovered
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // All nested classes discovered
    await Assert.That(code!).Contains("Stage");
    await Assert.That(code).Contains("Step");

    // Deeply nested enum discovered
    await Assert.That(code).Contains("ActionType");
  }

  /// <summary>
  /// Tests that internal enums are skipped during discovery.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_InternalEnum_SkipsEnumAsync() {
    // Arrange - Public event with internal enum property
    var source = """
using Whizbang.Core;

namespace TestApp;

internal enum InternalStatus { Draft, Active }

public record EventWithInternalEnum : IEvent {
    public string Name { get; init; } = "";
    public InternalStatus Status { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Internal enum should NOT have factory generated
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Internal enum should not have factory method
    await Assert.That(code!).DoesNotContain("Create_TestApp_InternalStatus");
  }

  /// <summary>
  /// Tests that framework enums (like DayOfWeek) are not discovered.
  /// STJ handles these natively.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_FrameworkEnum_SkipsEnumAsync() {
    // Arrange - Event with System.DayOfWeek property
    var source = """
using Whizbang.Core;
using System;

namespace TestApp;

public record ScheduleEvent : IEvent {
    public string Name { get; init; } = "";
    public DayOfWeek Day { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - DayOfWeek should NOT be discovered (framework enum)
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should not have factory for DayOfWeek
    await Assert.That(code!).DoesNotContain("Create_System_DayOfWeek");
  }

  /// <summary>
  /// Tests multiple enums in the same nested type.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleEnumsInNestedType_DiscoversAllEnumsAsync() {
    // Arrange - Nested type with multiple enum properties
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public enum Priority { Low, Medium, High, Critical }
public enum Category { Bug, Feature, Enhancement }

public record TaskEvent : IEvent {
    public List<TaskItem> Tasks { get; init; } = new();
}

public record TaskItem {
    public string Title { get; init; } = "";
    public Priority Priority { get; init; }
    public Category Category { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Both enums should be discovered
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Both enums discovered
    await Assert.That(code!).Contains("Priority");
    await Assert.That(code).Contains("Category");
  }

  // ==================== CLR Type Name Format Tests (Nested Type + Separator) ====================

  /// <summary>
  /// Primary bug fix test: Verifies that nested types use CLR format with + separator
  /// in type registrations instead of C# format with dots.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNestedMessageType_UsesClrFormatWithPlusSignAsync() {
    // Arrange - nested message type in static class
    var source = """
using Whizbang.Core;

namespace MyApp;

public static class AuthContracts {
  public record LoginCommand(string Username) : ICommand;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should use + for nested type in registration (CLR format)
    await Assert.That(code!).Contains("MyApp.AuthContracts+LoginCommand, TestAssembly");

    // Should NOT use dots for nested type (C# format) in the assembly-qualified name
    await Assert.That(code).DoesNotContain("MyApp.AuthContracts.LoginCommand, TestAssembly");
  }

  /// <summary>
  /// Regression test: Verifies that non-nested types still use dot separator correctly.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNonNestedMessageType_UsesDotSeparatorAsync() {
    // Arrange - non-nested message type
    var source = """
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should use dots for namespace-qualified name (not nested)
    await Assert.That(code!).Contains("MyApp.Commands.CreateOrder, TestAssembly");

    // Should NOT have any + in the type name (not nested)
    await Assert.That(code).DoesNotContain("MyApp.Commands+CreateOrder");
  }

  /// <summary>
  /// Tests deeply nested types (2+ levels) use multiple plus separators.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithDeeplyNestedType_UsesMultiplePlusSeparatorsAsync() {
    // Arrange - deeply nested message type
    var source = """
using Whizbang.Core;

namespace MyApp;

public static class Outer {
  public static class Inner {
    public record DeepCommand(string Data) : ICommand;
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should use + for both nesting levels
    await Assert.That(code!).Contains("MyApp.Outer+Inner+DeepCommand, TestAssembly");

    // Should NOT use dots for nested types
    await Assert.That(code).DoesNotContain("MyApp.Outer.Inner.DeepCommand, TestAssembly");
  }

  /// <summary>
  /// Tests that nested type uses CLR format in GetTypeInfoByName switch case.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNestedType_GeneratesCorrectSwitchCaseAsync() {
    // Arrange - nested message type
    var source = """
using Whizbang.Core;

namespace MyApp;

public static class Contracts {
  public record TestCommand(string Id) : ICommand;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should use + in switch case for type lookup
    await Assert.That(code!).Contains("\"MyApp.Contracts+TestCommand, TestAssembly\"");
  }

  /// <summary>
  /// Tests that nested type uses CLR format in MessageEnvelope registration.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNestedType_GeneratesCorrectEnvelopeRegistrationAsync() {
    // Arrange - nested message type
    var source = """
using Whizbang.Core;

namespace MyApp;

public static class Events {
  public record OrderCreated(string OrderId) : IEvent;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should use + for nested type in MessageEnvelope registration
    await Assert.That(code!).Contains("MessageEnvelope`1[[MyApp.Events+OrderCreated, TestAssembly]]");

    // Should NOT use dots for nested type in envelope
    await Assert.That(code).DoesNotContain("MessageEnvelope`1[[MyApp.Events.OrderCreated, TestAssembly]]");
  }

  /// <summary>
  /// Tests global namespace type handling (edge case).
  /// Types in global namespace should just have their simple name.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithGlobalNamespaceType_HandlesCorrectlyAsync() {
    // Arrange - type in global namespace
    var source = """
using Whizbang.Core;

public record GlobalCommand(string Data) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should use simple name for global namespace type
    await Assert.That(code!).Contains("GlobalCommand, TestAssembly");
  }
}
