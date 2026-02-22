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

  // ==================== Direct Property Nested Type Discovery Tests ====================

  /// <summary>
  /// Tests direct property nested type discovery: non-collection properties should be discovered.
  /// Example: MessageContent Content (not List&lt;MessageContent&gt;) should still discover MessageContent.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithDirectPropertyNestedType_DiscoversNestedTypeAsync() {
    // Arrange - Event with direct property (not a collection) that references a nested type
    var source = """
using Whizbang.Core;

namespace TestApp;

public record ParentEvent : IEvent {
    public string Id { get; init; } = "";
    public ChildModel Child { get; init; } = new();
}

public record ChildModel {
    public string Name { get; init; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - ChildModel should be discovered even though it's a direct property, not a collection
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("ParentEvent");
    // This is the critical assertion - ChildModel should be discovered
    await Assert.That(code).Contains("ChildModel");
  }

  /// <summary>
  /// Tests deep direct property nesting: A → B → C should discover both B and C.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithDeepDirectPropertyNesting_DiscoversAllTypesAsync() {
    // Arrange - Event with chain of direct properties: TopMessage → MiddleModel → DeepModel
    var source = """
using Whizbang.Core;

namespace TestApp;

public record TopMessage : ICommand {
    public string Id { get; init; } = "";
    public MiddleModel Middle { get; init; } = new();
}

public record MiddleModel {
    public string Name { get; init; } = "";
    public DeepModel Deep { get; init; } = new();
}

public record DeepModel {
    public decimal Value { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Both MiddleModel and DeepModel should be discovered recursively
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("TopMessage");
    await Assert.That(code).Contains("MiddleModel");
    await Assert.That(code).Contains("DeepModel");
  }

  /// <summary>
  /// Tests mixed scenario: message with both collection and direct property nested types.
  /// Both should be discovered correctly.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMixedCollectionAndDirectNestedTypes_DiscoversAllTypesAsync() {
    // Arrange - Event with both List<CollectionItem> and DirectItem
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record MixedEvent : IEvent {
    public List<CollectionItem> Items { get; init; } = new();
    public DirectItem Direct { get; init; } = new();
}

public record CollectionItem {
    public string CollectionValue { get; init; } = "";
}

public record DirectItem {
    public string DirectValue { get; init; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Both CollectionItem AND DirectItem should be discovered
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("MixedEvent");
    await Assert.That(code).Contains("CollectionItem");  // From List<T>
    await Assert.That(code).Contains("DirectItem");      // From direct property
  }

  // ==================== Struct Nested Type Discovery Tests ====================

  /// <summary>
  /// Tests that get-only properties (not just init-only) get null setters.
  /// This tests the root cause fix: p.SetMethod?.IsInitOnly ?? false was wrong
  /// because { get; } properties have SetMethod == null, not IsInitOnly == true.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractMessageTypeInfo</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithGetOnlyProperty_UsesNullSetterAsync() {
    // Arrange - Event with a nested type that has a get-only property
    var source = """
using Whizbang.Core;

namespace TestApp;

public record GetOnlyEvent : IEvent {
    public string Id { get; init; } = "";
    public GetOnlyModel Data { get; init; } = new("default");
}

// Simulates Permission pattern: get-only property with constructor
public class GetOnlyModel {
    public string Value { get; }  // GET-ONLY - no setter at all!
    public GetOnlyModel(string value) => Value = value;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors from trying to set readonly property
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    // Verify it uses null setter, not property assignment
    await Assert.That(code!).DoesNotContain("GetOnlyModel)obj).Value = ");
  }

  /// <summary>
  /// Tests record struct nested type discovery with primary constructor.
  /// Structs should be discovered and have factory methods generated.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithRecordStructNestedType_DiscoversStructAsync() {
    // Arrange - Event with record struct direct property
    var source = """
using Whizbang.Core;

namespace TestApp;

public record ParentEvent : IEvent {
    public string Id { get; init; } = "";
    public NestedStruct Data { get; init; }
}

public readonly record struct NestedStruct(string Value);
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - NestedStruct discovered and factory method generated
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("NestedStruct");
    await Assert.That(code).Contains("Create_TestApp_NestedStruct");
  }

  /// <summary>
  /// Tests readonly record struct with get-only property uses constructor initialization.
  /// This is the complete test for struct support: discovery + correct code generation.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithReadonlyRecordStruct_UsesConstructorInitializationAsync() {
    // Arrange - Command with readonly record struct property
    var source = """
using Whizbang.Core;

namespace TestApp;

public record MessageWithPermission : ICommand {
    public string Id { get; init; } = "";
    public PermissionValue Permission { get; init; }
}

public readonly record struct PermissionValue(string Value);
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors and uses constructor, not setters
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    // Verify constructor-based creation
    await Assert.That(code!).Contains("new global::TestApp.PermissionValue(");
    // Verify no property setter generated
    await Assert.That(code).DoesNotContain("PermissionValue)obj).Value = ");
  }

  /// <summary>
  /// Tests that nested collections (List&lt;List&lt;T&gt;&gt;) don't cause invalid factory methods.
  /// The element type of List&lt;List&lt;T&gt;&gt; is List&lt;T&gt; which is a System.* type
  /// and should be skipped, not have a factory method generated.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNestedCollections_SkipsSystemTypesAsync() {
    // Arrange - Event with nested collection (List<List<T>>)
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record NestedCollectionEvent : IEvent {
    public string Id { get; init; } = "";
    public List<List<string>> Matrix { get; init; } = new();
    public List<List<CustomItem>> CustomMatrix { get; init; } = new();
}

public record CustomItem(string Value);
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors (no invalid factory methods for List<T>)
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should have factory for CustomItem (the innermost custom type)
    await Assert.That(code!).Contains("CustomItem");

    // Should NOT have factory for List<T> (System.* type)
    await Assert.That(code).DoesNotContain("Create_System_Collections_Generic_List");
    await Assert.That(code).DoesNotContain("_List_System_Collections");
  }

  /// <summary>
  /// Tests that computed read-only properties (expression-bodied) are excluded from
  /// constructor parameters and object initializers.
  /// Properties like `public bool HasFiles => Files.Count > 0` cannot be assigned to.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithComputedReadOnlyProperty_ExcludesFromConstructorAsync() {
    // Arrange - Class with computed read-only property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record FileContext : ICommand {
    public string Id { get; init; } = "";
    public List<string> Files { get; init; } = new();
    public bool HasFiles => Files.Count > 0;  // Computed read-only property
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // HasFiles should NOT be in the object initializer (it's computed/read-only and cannot be assigned)
    // The ObjectWithParameterizedConstructorCreator should NOT include: HasFiles = (bool)args[x]
    await Assert.That(code!).DoesNotContain("HasFiles = (bool)args");

    // HasFiles should also NOT have a setter lambda
    await Assert.That(code).DoesNotContain("FileContext)obj).HasFiles = ");
  }

  /// <summary>
  /// Tests that abstract types are not instantiated directly.
  /// Abstract classes cannot be created with 'new' - the generator should skip
  /// generating factory methods for abstract types or use polymorphic handling.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithAbstractNestedType_SkipsDirectInstantiationAsync() {
    // Arrange - Message with abstract type property
    var source = """
using Whizbang.Core;

namespace TestApp;

public record MessageWithAbstract : ICommand {
    public string Id { get; init; } = "";
    public AbstractFieldSettings Settings { get; init; } = null!;
}

public abstract class AbstractFieldSettings {
    public string Name { get; init; } = "";
}

public class ConcreteFieldSettings : AbstractFieldSettings {
    public string Value { get; init; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors (shouldn't try to instantiate abstract type)
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should NOT try to instantiate abstract class with 'new'
    await Assert.That(code!).DoesNotContain("new global::TestApp.AbstractFieldSettings()");
    await Assert.That(code).DoesNotContain("new global::TestApp.AbstractFieldSettings(");
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

  // ==================== WhizbangId Skip Tests ====================

  /// <summary>
  /// Tests that types with [WhizbangId] attribute are skipped during nested type discovery.
  /// WhizbangId types have their own converters generated by WhizbangIdGenerator
  /// and should NOT have JsonTypeInfo generated by MessageJsonContextGenerator.
  /// This prevents incorrect empty-object metadata from overriding proper converter handling.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_hasWhizbangIdAttribute</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithWhizbangIdProperty_SkipsConverterGenerationAsync() {
    // Arrange - Message with a property using [WhizbangId] type
    var source = """
using Whizbang.Core;

namespace TestApp;

[WhizbangId]
public readonly partial struct ProductId;

public record CreateProductCommand(ProductId ProductId, string Name) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // The message itself should be discovered
    await Assert.That(code!).Contains("CreateProductCommand");

    // ProductId should NOT have a factory method generated
    // (would create incorrect empty-object metadata)
    await Assert.That(code).DoesNotContain("Create_TestApp_ProductId");
    await Assert.That(code).DoesNotContain("_TestApp_ProductId");
  }

  /// <summary>
  /// Tests that multiple WhizbangId types in a single message are all skipped.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_hasWhizbangIdAttribute</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleWhizbangIdProperties_SkipsAllConvertersAsync() {
    // Arrange - Message with multiple [WhizbangId] types
    var source = """
using Whizbang.Core;

namespace TestApp;

[WhizbangId]
public readonly partial struct OrderId;

[WhizbangId]
public readonly partial struct CustomerId;

[WhizbangId]
public readonly partial struct ProductId;

public record CreateOrderCommand(OrderId OrderId, CustomerId CustomerId, ProductId ProductId, string Details) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // The message itself should be discovered
    await Assert.That(code!).Contains("CreateOrderCommand");

    // None of the WhizbangId types should have factory methods
    await Assert.That(code).DoesNotContain("Create_TestApp_OrderId");
    await Assert.That(code).DoesNotContain("Create_TestApp_CustomerId");
    await Assert.That(code).DoesNotContain("Create_TestApp_ProductId");
  }

  /// <summary>
  /// Tests that WhizbangId types in collections use GetOrCreateTypeInfo delegation.
  /// The List&lt;WhizbangIdType&gt; factory is generated but delegates element info to WhizbangIdJsonContext.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_hasWhizbangIdAttribute</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithWhizbangIdInCollection_UsesTypeInfoDelegationAsync() {
    // Arrange - Message with List<WhizbangIdType>
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

[WhizbangId]
public readonly partial struct ItemId;

public record ProcessItemsCommand(List<ItemId> ItemIds, string BatchName) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // The message itself should be discovered
    await Assert.That(code!).Contains("ProcessItemsCommand");

    // ItemId should NOT have a direct factory method (Create_TestApp_ItemId)
    // because it has its own converter from WhizbangIdGenerator
    await Assert.That(code).DoesNotContain("Create_TestApp_ItemId(");

    // List<ItemId> SHOULD have a factory (it needs to be serializable)
    await Assert.That(code).Contains("CreateList_TestApp_ItemId");

    // But it should use GetOrCreateTypeInfo for element info (delegates to WhizbangIdJsonContext)
    await Assert.That(code).Contains("GetOrCreateTypeInfo<global::TestApp.ItemId>(options)");
  }

  /// <summary>
  /// Tests that non-WhizbangId struct types ARE still discovered (regression test).
  /// Only types with [WhizbangId] attribute should be skipped.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_hasWhizbangIdAttribute</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNonWhizbangIdStruct_StillDiscoveredAsync() {
    // Arrange - Message with regular struct (no [WhizbangId])
    var source = """
using Whizbang.Core;

namespace TestApp;

// Regular struct without [WhizbangId] - should be discovered
public readonly record struct GeoCoordinate(double Latitude, double Longitude);

public record LocationCommand(GeoCoordinate Location, string Name) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // The message should be discovered
    await Assert.That(code!).Contains("LocationCommand");

    // GeoCoordinate SHOULD have a factory (not a WhizbangId)
    await Assert.That(code).Contains("Create_TestApp_GeoCoordinate");
  }

  // ==================== Nullable Value Type List Tests ====================

  /// <summary>
  /// Primary bug fix test: Verifies that List&lt;Guid?&gt; is generated correctly.
  /// The bug was that _discoverListTypes skipped ALL System.* types including Guid?,
  /// when it should only skip collection types (List&lt;List&lt;T&gt;&gt;).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableGuid_GeneratesListFactoryAsync() {
    // Arrange - Message with List<Guid?> property
    var source = """
using Whizbang.Core;
using System;
using System.Collections.Generic;

namespace TestApp;

public record ProcessIdsCommand(List<Guid?> OptionalIds) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<Guid?> factory method SHOULD be generated
    // ElementUniqueIdentifier: "global::System.Guid?" -> "System_Guid__Nullable"
    await Assert.That(code!).Contains("List<global::System.Guid?>");
    await Assert.That(code).Contains("CreateList_System_Guid__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;int?&gt; is generated correctly.
  /// Generator normalizes 'int' keyword alias to 'System.Int32' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableInt_GeneratesListFactoryAsync() {
    // Arrange - Message with List<int?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessCountsCommand(List<int?> OptionalCounts) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<int?> factory method SHOULD be generated
    // Generator normalizes 'int' -> 'global::System.Int32' for consistent naming
    await Assert.That(code!).Contains("List<global::System.Int32?>");
    await Assert.That(code).Contains("CreateList_System_Int32__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;DateTime?&gt; is generated correctly.
  /// DateTime has no C# keyword alias, so uses fully qualified name.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableDateTime_GeneratesListFactoryAsync() {
    // Arrange - Message with List<DateTime?> property
    var source = """
using Whizbang.Core;
using System;
using System.Collections.Generic;

namespace TestApp;

public record ProcessDatesCommand(List<DateTime?> OptionalDates) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<DateTime?> factory method SHOULD be generated
    // DateTime has no keyword alias - uses fully qualified name
    await Assert.That(code!).Contains("List<global::System.DateTime?>");
    await Assert.That(code).Contains("CreateList_System_DateTime__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;decimal?&gt; is generated correctly.
  /// Generator normalizes 'decimal' keyword alias to 'System.Decimal' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableDecimal_GeneratesListFactoryAsync() {
    // Arrange - Message with List<decimal?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessAmountsCommand(List<decimal?> OptionalAmounts) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<decimal?> factory method SHOULD be generated
    // Generator normalizes 'decimal' -> 'global::System.Decimal' for consistent naming
    await Assert.That(code!).Contains("List<global::System.Decimal?>");
    await Assert.That(code).Contains("CreateList_System_Decimal__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;DateTimeOffset?&gt; is generated correctly.
  /// DateTimeOffset has no C# keyword alias, so uses fully qualified name.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableDateTimeOffset_GeneratesListFactoryAsync() {
    // Arrange - Message with List<DateTimeOffset?> property
    var source = """
using Whizbang.Core;
using System;
using System.Collections.Generic;

namespace TestApp;

public record ProcessTimestampsCommand(List<DateTimeOffset?> OptionalTimestamps) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<DateTimeOffset?> factory method SHOULD be generated
    // DateTimeOffset has no keyword alias - uses fully qualified name
    await Assert.That(code!).Contains("List<global::System.DateTimeOffset?>");
    await Assert.That(code).Contains("CreateList_System_DateTimeOffset__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;bool?&gt; is generated correctly.
  /// Generator normalizes 'bool' keyword alias to 'System.Boolean' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableBool_GeneratesListFactoryAsync() {
    // Arrange - Message with List<bool?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessFlagsCommand(List<bool?> OptionalFlags) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<bool?> factory method SHOULD be generated
    // Generator normalizes 'bool' -> 'global::System.Boolean' for consistent naming
    await Assert.That(code!).Contains("List<global::System.Boolean?>");
    await Assert.That(code).Contains("CreateList_System_Boolean__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;long?&gt; is generated correctly.
  /// Generator normalizes 'long' keyword alias to 'System.Int64' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableLong_GeneratesListFactoryAsync() {
    // Arrange - Message with List<long?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessLongIdsCommand(List<long?> OptionalIds) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<long?> factory method SHOULD be generated
    // Generator normalizes 'long' -> 'global::System.Int64' for consistent naming
    await Assert.That(code!).Contains("List<global::System.Int64?>");
    await Assert.That(code).Contains("CreateList_System_Int64__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;short?&gt; is generated correctly.
  /// Generator normalizes 'short' keyword alias to 'System.Int16' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableShort_GeneratesListFactoryAsync() {
    // Arrange - Message with List<short?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessShortValuesCommand(List<short?> OptionalValues) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<short?> factory method SHOULD be generated
    // Generator normalizes 'short' -> 'global::System.Int16' for consistent naming
    await Assert.That(code!).Contains("List<global::System.Int16?>");
    await Assert.That(code).Contains("CreateList_System_Int16__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;byte?&gt; is generated correctly.
  /// Generator normalizes 'byte' keyword alias to 'System.Byte' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableByte_GeneratesListFactoryAsync() {
    // Arrange - Message with List<byte?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessBytesCommand(List<byte?> OptionalBytes) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<byte?> factory method SHOULD be generated
    // Generator normalizes 'byte' -> 'global::System.Byte' for consistent naming
    await Assert.That(code!).Contains("List<global::System.Byte?>");
    await Assert.That(code).Contains("CreateList_System_Byte__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;float?&gt; is generated correctly.
  /// Generator normalizes 'float' keyword alias to 'System.Single' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableFloat_GeneratesListFactoryAsync() {
    // Arrange - Message with List<float?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessFloatsCommand(List<float?> OptionalFloats) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<float?> factory method SHOULD be generated
    // Generator normalizes 'float' -> 'global::System.Single' for consistent naming
    await Assert.That(code!).Contains("List<global::System.Single?>");
    await Assert.That(code).Contains("CreateList_System_Single__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;double?&gt; is generated correctly.
  /// Generator normalizes 'double' keyword alias to 'System.Double' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableDouble_GeneratesListFactoryAsync() {
    // Arrange - Message with List<double?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessDoublesCommand(List<double?> OptionalDoubles) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<double?> factory method SHOULD be generated
    // Generator normalizes 'double' -> 'global::System.Double' for consistent naming
    await Assert.That(code!).Contains("List<global::System.Double?>");
    await Assert.That(code).Contains("CreateList_System_Double__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;char?&gt; is generated correctly.
  /// Generator normalizes 'char' keyword alias to 'System.Char' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableChar_GeneratesListFactoryAsync() {
    // Arrange - Message with List<char?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessCharsCommand(List<char?> OptionalChars) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<char?> factory method SHOULD be generated
    // Generator normalizes 'char' -> 'global::System.Char' for consistent naming
    await Assert.That(code!).Contains("List<global::System.Char?>");
    await Assert.That(code).Contains("CreateList_System_Char__Nullable");
  }

  /// <summary>
  /// Tests that multiple nullable value type lists in one message are all generated.
  /// Verifies that all keyword aliases are normalized to fully qualified names.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleNullableValueTypeLists_GeneratesAllFactoriesAsync() {
    // Arrange - Message with multiple nullable value type lists
    var source = """
using Whizbang.Core;
using System;
using System.Collections.Generic;

namespace TestApp;

public record ProcessMixedCommand(
    List<Guid?> OptionalIds,
    List<int?> OptionalCounts,
    List<DateTime?> OptionalDates,
    List<decimal?> OptionalAmounts
) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // All nullable value type list factories SHOULD be generated with fully qualified names
    await Assert.That(code!).Contains("List<global::System.Guid?>");
    await Assert.That(code).Contains("List<global::System.Int32?>");
    await Assert.That(code).Contains("List<global::System.DateTime?>");
    await Assert.That(code).Contains("List<global::System.Decimal?>");
  }

  /// <summary>
  /// Regression test: Verifies that nested collections (List&lt;List&lt;T&gt;&gt;) are still skipped.
  /// The fix for nullable value types should not break handling of nested collections.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNestedCollections_StillSkipsCollectionTypesAsync() {
    // Arrange - Message with nested collection (should be skipped)
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record MatrixCommand(List<List<int>> Matrix) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should NOT have factory for List<List<int>> (nested collections skipped)
    // The element type is System.Collections.Generic.List<int> which should be skipped
    await Assert.That(code!).DoesNotContain("CreateList_System_Collections_Generic_List");
  }

  /// <summary>
  /// Tests that List&lt;uint?&gt; is generated correctly.
  /// Generator normalizes 'uint' keyword alias to 'System.UInt32' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableUInt_GeneratesListFactoryAsync() {
    // Arrange - Message with List<uint?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessUIntCommand(List<uint?> OptionalValues) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<uint?> factory method SHOULD be generated
    // Generator normalizes 'uint' -> 'global::System.UInt32' for consistent naming
    await Assert.That(code!).Contains("List<global::System.UInt32?>");
    await Assert.That(code).Contains("CreateList_System_UInt32__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;ulong?&gt; is generated correctly.
  /// Generator normalizes 'ulong' keyword alias to 'System.UInt64' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableULong_GeneratesListFactoryAsync() {
    // Arrange - Message with List<ulong?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessULongCommand(List<ulong?> OptionalValues) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<ulong?> factory method SHOULD be generated
    // Generator normalizes 'ulong' -> 'global::System.UInt64' for consistent naming
    await Assert.That(code!).Contains("List<global::System.UInt64?>");
    await Assert.That(code).Contains("CreateList_System_UInt64__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;ushort?&gt; is generated correctly.
  /// Generator normalizes 'ushort' keyword alias to 'System.UInt16' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableUShort_GeneratesListFactoryAsync() {
    // Arrange - Message with List<ushort?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessUShortCommand(List<ushort?> OptionalValues) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<ushort?> factory method SHOULD be generated
    // Generator normalizes 'ushort' -> 'global::System.UInt16' for consistent naming
    await Assert.That(code!).Contains("List<global::System.UInt16?>");
    await Assert.That(code).Contains("CreateList_System_UInt16__Nullable");
  }

  /// <summary>
  /// Tests that List&lt;sbyte?&gt; is generated correctly.
  /// Generator normalizes 'sbyte' keyword alias to 'System.SByte' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableSByte_GeneratesListFactoryAsync() {
    // Arrange - Message with List<sbyte?> property
    var source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessSByteCommand(List<sbyte?> OptionalValues) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<sbyte?> factory method SHOULD be generated
    // Generator normalizes 'sbyte' -> 'global::System.SByte' for consistent naming
    await Assert.That(code!).Contains("List<global::System.SByte?>");
    await Assert.That(code).Contains("CreateList_System_SByte__Nullable");
  }

  /// <summary>
  /// Tests the distinction between nullable value types (should be generated)
  /// and nested collections (should be skipped) in the same message.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMixOfNullableValueTypesAndNestedCollections_HandlesCorrectlyAsync() {
    // Arrange - Message with both nullable value type list AND nested collection
    var source = """
using Whizbang.Core;
using System;
using System.Collections.Generic;

namespace TestApp;

public record MixedCollectionsCommand(
    List<Guid?> OptionalIds,           // Should be generated
    List<List<string>> NestedStrings   // Should be skipped
) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // List<Guid?> SHOULD be generated (nullable value type)
    await Assert.That(code!).Contains("List<global::System.Guid?>");
    await Assert.That(code).Contains("CreateList_System_Guid__Nullable");

    // List<List<string>> should NOT have factory (nested collection)
    await Assert.That(code).DoesNotContain("CreateList_System_Collections");
  }

  // ==================== Inherited Property Tests ====================

  /// <summary>
  /// Tests that properties from a base class are included in generated JSON serialization.
  /// This is the core bug fix: GetMembers() only returns direct members, not inherited.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_getAllPropertiesIncludingInherited</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithInheritedProperties_IncludesBaseClassPropertiesAsync() {
    // Arrange - Command that extends a base class with properties
    var source = """
using Whizbang.Core;
using System;

namespace TestApp;

// Base class with properties
public class BaseCommand {
  public Guid StreamId { get; set; }
  public string? CorrelationId { get; set; }
}

// Derived command that inherits base properties
public class DerivedCommand : BaseCommand, ICommand {
  public string Name { get; set; } = string.Empty;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // CRITICAL: All 3 properties should be included - both inherited and direct
    // StreamId from base
    await Assert.That(code!).Contains("\"StreamId\"");
    // CorrelationId from base
    await Assert.That(code).Contains("\"CorrelationId\"");
    // Name from derived
    await Assert.That(code).Contains("\"Name\"");
  }

  /// <summary>
  /// Tests that inherited properties appear before derived class properties in generated code.
  /// Order matters for JSON serialization consistency.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_getAllPropertiesIncludingInherited</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithInheritedProperties_BasePropertiesAppearFirstAsync() {
    // Arrange - Command with clear ordering requirement
    var source = """
using Whizbang.Core;
using System;

namespace TestApp;

public class BaseCommand {
  public Guid BaseId { get; set; }
}

public class DerivedCommand : BaseCommand, ICommand {
  public string DerivedProp { get; set; } = string.Empty;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // BaseId should appear before DerivedProp in the generated code
    var baseIdIndex = code!.IndexOf("\"BaseId\"", StringComparison.Ordinal);
    var derivedPropIndex = code.IndexOf("\"DerivedProp\"", StringComparison.Ordinal);

    await Assert.That(baseIdIndex).IsGreaterThan(-1);
    await Assert.That(derivedPropIndex).IsGreaterThan(-1);
    await Assert.That(baseIdIndex).IsLessThan(derivedPropIndex);
  }

  /// <summary>
  /// Tests multi-level inheritance (grandparent -> parent -> child).
  /// All properties from all levels should be included.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_getAllPropertiesIncludingInherited</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultiLevelInheritance_IncludesAllLevelsAsync() {
    // Arrange - Three-level inheritance hierarchy
    var source = """
using Whizbang.Core;
using System;

namespace TestApp;

// Level 1 - Grandparent
public class GrandparentCommand {
  public Guid GrandparentId { get; set; }
}

// Level 2 - Parent
public class ParentCommand : GrandparentCommand {
  public string ParentProp { get; set; } = string.Empty;
}

// Level 3 - Child (the actual command)
public class ChildCommand : ParentCommand, ICommand {
  public int ChildProp { get; set; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // All 3 properties from all levels should be present
    await Assert.That(code!).Contains("\"GrandparentId\"");
    await Assert.That(code).Contains("\"ParentProp\"");
    await Assert.That(code).Contains("\"ChildProp\"");
  }

  /// <summary>
  /// Tests that virtual properties that are overridden in derived class use the derived property.
  /// Only one property should appear in the output (no duplicates).
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_getAllPropertiesIncludingInherited</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithVirtualOverride_UsesOnlyDerivedPropertyAsync() {
    // Arrange - Base with virtual property, derived with override
    var source = """
using Whizbang.Core;

namespace TestApp;

public class BaseWithVirtual {
  public virtual string Name { get; set; } = string.Empty;
}

public class DerivedWithOverride : BaseWithVirtual, ICommand {
  public override string Name { get; set; } = "overridden";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // "Name" should appear exactly once - count occurrences in property definitions
    // Looking for pattern in CreateProperty call: CreateProperty<...>(options, "Name", ...)
    var matches = System.Text.RegularExpressions.Regex.Matches(code!, @"CreateProperty<[^>]+>\(\s*options,\s*""Name""");
    await Assert.That(matches.Count).IsEqualTo(1);
  }

  /// <summary>
  /// Tests that property hiding with 'new' keyword uses the derived class property.
  /// Only one property should appear in the output (no duplicates).
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_getAllPropertiesIncludingInherited</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPropertyHidingNew_UsesOnlyDerivedPropertyAsync() {
    // Arrange - Base with property, derived hides with 'new'
    var source = """
using Whizbang.Core;

namespace TestApp;

public class BaseWithProp {
  public string Value { get; set; } = string.Empty;
}

public class DerivedWithNew : BaseWithProp, ICommand {
  public new string Value { get; set; } = "new";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // "Value" should appear exactly once in property definitions
    // Looking for pattern in CreateProperty call: CreateProperty<...>(options, "Value", ...)
    var matches = System.Text.RegularExpressions.Regex.Matches(code!, @"CreateProperty<[^>]+>\(\s*options,\s*""Value""");
    await Assert.That(matches.Count).IsEqualTo(1);
  }

  /// <summary>
  /// Tests that static properties from base class are NOT included.
  /// Only instance properties should be serialized.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_getAllPropertiesIncludingInherited</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithInheritedStaticProperty_ExcludesStaticAsync() {
    // Arrange - Base with static property
    var source = """
using Whizbang.Core;

namespace TestApp;

public class BaseWithStatic {
  public static string StaticProp { get; set; } = string.Empty;
  public string InstanceProp { get; set; } = string.Empty;
}

public class DerivedCommand : BaseWithStatic, ICommand {
  public int DerivedProp { get; set; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Static property should NOT be included
    await Assert.That(code!).DoesNotContain("\"StaticProp\"");
    // Instance properties should be included
    await Assert.That(code).Contains("\"InstanceProp\"");
    await Assert.That(code).Contains("\"DerivedProp\"");
  }

  /// <summary>
  /// Tests that private/internal properties from base class are NOT included.
  /// Only public properties should be serialized.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_getAllPropertiesIncludingInherited</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithInheritedNonPublicProperties_ExcludesNonPublicAsync() {
    // Arrange - Base with private and internal properties
    var source = """
using Whizbang.Core;

namespace TestApp;

public class BaseWithNonPublic {
  public string PublicProp { get; set; } = string.Empty;
  internal string InternalProp { get; set; } = string.Empty;
  protected string ProtectedProp { get; set; } = string.Empty;
  private string PrivateProp { get; set; } = string.Empty;
}

public class DerivedCommand : BaseWithNonPublic, ICommand {
  public int DerivedProp { get; set; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Only public properties should be included
    await Assert.That(code!).Contains("\"PublicProp\"");
    await Assert.That(code).Contains("\"DerivedProp\"");
    // Non-public properties should NOT be included
    await Assert.That(code).DoesNotContain("\"InternalProp\"");
    await Assert.That(code).DoesNotContain("\"ProtectedProp\"");
    await Assert.That(code).DoesNotContain("\"PrivateProp\"");
  }

  /// <summary>
  /// Tests that read-only properties (no setter) from base class are included.
  /// These properties can be deserialized via constructor or init.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_getAllPropertiesIncludingInherited</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithInheritedReadOnlyProperty_IncludesReadOnlyAsync() {
    // Arrange - Base with read-only property
    var source = """
using Whizbang.Core;

namespace TestApp;

public class BaseWithReadOnly {
  public string ReadOnlyProp { get; } = "readonly";
  public string ReadWriteProp { get; set; } = string.Empty;
}

public class DerivedCommand : BaseWithReadOnly, ICommand {
  public int DerivedProp { get; set; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Both read-only and read-write properties should be included
    await Assert.That(code!).Contains("\"ReadOnlyProp\"");
    await Assert.That(code).Contains("\"ReadWriteProp\"");
    await Assert.That(code).Contains("\"DerivedProp\"");
  }

  /// <summary>
  /// Tests that a command without inheritance still works correctly (regression test).
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_getAllPropertiesIncludingInherited</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNoInheritance_WorksUnchangedAsync() {
    // Arrange - Simple command without inheritance
    var source = """
using Whizbang.Core;
using System;

namespace TestApp;

public class SimpleCommand : ICommand {
  public Guid Id { get; set; }
  public string Name { get; set; } = string.Empty;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Both properties should be included
    await Assert.That(code!).Contains("\"Id\"");
    await Assert.That(code).Contains("\"Name\"");
  }

  /// <summary>
  /// Tests record inheritance works correctly.
  /// Records are commonly used for commands/events.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_getAllPropertiesIncludingInherited</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithRecordInheritance_IncludesBasePropertiesAsync() {
    // Arrange - Record that inherits from another record
    var source = """
using Whizbang.Core;
using System;

namespace TestApp;

public abstract record BaseEvent(Guid EventId, string EventType);

public record DerivedEvent(Guid EventId, string EventType, string Payload) : BaseEvent(EventId, EventType), IEvent;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // All properties should be included
    await Assert.That(code!).Contains("\"EventId\"");
    await Assert.That(code).Contains("\"EventType\"");
    await Assert.That(code).Contains("\"Payload\"");
  }

  /// <summary>
  /// Tests that properties from object base type are NOT included.
  /// Object has no serializable properties anyway, but we verify we stop there.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_getAllPropertiesIncludingInherited</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_StopsAtObjectBaseType_NoObjectPropertiesAsync() {
    // Arrange - Command that directly extends object (implicitly)
    var source = """
using Whizbang.Core;

namespace TestApp;

public class SimpleCommand : ICommand {
  public string Prop { get; set; } = string.Empty;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Only our property, not any object internals
    await Assert.That(code!).Contains("\"Prop\"");
    // System.Object doesn't have public serializable properties, but just verify no weird ones
    await Assert.That(code).DoesNotContain("\"GetType\"");
    await Assert.That(code).DoesNotContain("\"GetHashCode\"");
  }
}
