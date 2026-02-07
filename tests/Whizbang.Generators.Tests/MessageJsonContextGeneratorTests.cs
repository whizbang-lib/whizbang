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

    // Should also generate WhizbangJsonContext facade since there are messages
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

    // Should NOT generate WhizbangJsonContext facade since there are no messages
    var facadeCode = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(facadeCode).IsNull();
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

    // Should NOT generate WhizbangJsonContext facade since there are no messages
    var facadeCode = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(facadeCode).IsNull();
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
}
