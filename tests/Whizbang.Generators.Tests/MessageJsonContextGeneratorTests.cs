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
    const string source = @"
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
    await Assert.That(messageCode).Contains("namespace TestAssembly.Generated");
    await Assert.That(messageCode).Contains("public partial class MessageJsonContext : JsonSerializerContext");

    // Should always generate WhizbangJsonContext facade
    var facadeCode = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(facadeCode).IsNotNull();
    await Assert.That(facadeCode).Contains("public class WhizbangJsonContext : JsonSerializerContext, IJsonTypeInfoResolver");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithCommand_GeneratesMessageEnvelopeFactoryAsync() {
    // Arrange
    const string source = @"
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
    await Assert.That(code).Contains("MessageEnvelope<global::MyApp.Commands.CreateOrder>");
    await Assert.That(code).Contains("CreateMessageEnvelope");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleMessages_GeneratesAllFactoriesAsync() {
    // Arrange
    const string source = @"
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
    await Assert.That(code).Contains("MessageEnvelope<global::MyApp.Commands.CreateOrder>");
    await Assert.That(code).Contains("MessageEnvelope<global::MyApp.Commands.UpdateOrder>");
    await Assert.That(code).Contains("MessageEnvelope<global::MyApp.Events.OrderCreated>");
    await Assert.That(code).Contains("MessageEnvelope<global::MyApp.Events.OrderUpdated>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesGetTypeInfoSwitchAsync() {
    // Arrange
    const string source = @"
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
    await Assert.That(code).Contains("public override JsonTypeInfo? GetTypeInfo(Type type)");
    await Assert.That(code).Contains("if (type == typeof(");
    await Assert.That(code).Contains("return null");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesMessageEnvelopeFactoryMethodAsync() {
    // Arrange
    const string source = @"
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
    await Assert.That(code).Contains("CreateMessageEnvelope_MyApp_Commands_CreateOrder");
    await Assert.That(code).Contains("MessageEnvelope<global::MyApp.Commands.CreateOrder>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesPropertyHelperMethodAsync() {
    // Arrange
    const string source = @"
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
    await Assert.That(code).Contains("CreateProperty<TProperty>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesCoreValueObjectFactoriesAsync() {
    // Arrange - No user message types, but should still generate core types
    const string source = @"
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
    await Assert.That(code).Contains("Create_MessageId");
    await Assert.That(code).Contains("Create_CorrelationId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesGetTypeInfoInternalMethodAsync() {
    // Arrange
    const string source = @"
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
    await Assert.That(code).Contains("private JsonTypeInfo? GetTypeInfoInternal(Type type, JsonSerializerOptions options)");
    await Assert.That(code).Contains("return Create_MessageId(options);");
    await Assert.That(code).Contains("return Create_CorrelationId(options);");
  }

  /// <summary>
  /// Tests that GetTypeInfoInternal includes handling for primitive types like Guid, int, string.
  /// This is critical for serialization when STJ calls GetTypeInfo(typeof(Guid)) during envelope serialization.
  /// Primitives are handled directly using JsonMetadataServices (not GetOrCreateTypeInfo) to avoid
  /// false circular reference detection since the caller has already added the type to TypesBeingCreated.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesPrimitiveTypeHandlingInGetTypeInfoInternalAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string Name, int Quantity, System.Guid OrderId) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate primitive type handling in GetTypeInfoInternal using JsonMetadataServices directly
    // (NOT GetOrCreateTypeInfo to avoid false circular reference detection)
    await Assert.That(code).Contains("if (type == typeof(string)) return JsonMetadataServices.CreateValueInfo<string>(options, JsonMetadataServices.StringConverter);");
    await Assert.That(code).Contains("if (type == typeof(int)) return JsonMetadataServices.CreateValueInfo<int>(options, JsonMetadataServices.Int32Converter);");
    await Assert.That(code).Contains("if (type == typeof(Guid)) return JsonMetadataServices.CreateValueInfo<Guid>(options, JsonMetadataServices.GuidConverter);");
    await Assert.That(code).Contains("if (type == typeof(long)) return JsonMetadataServices.CreateValueInfo<long>(options, JsonMetadataServices.Int64Converter);");
    await Assert.That(code).Contains("if (type == typeof(bool)) return JsonMetadataServices.CreateValueInfo<bool>(options, JsonMetadataServices.BooleanConverter);");
    await Assert.That(code).Contains("if (type == typeof(DateTime)) return JsonMetadataServices.CreateValueInfo<DateTime>(options, JsonMetadataServices.DateTimeConverter);");
    await Assert.That(code).Contains("if (type == typeof(DateTimeOffset)) return JsonMetadataServices.CreateValueInfo<DateTimeOffset>(options, new global::Whizbang.Core.Serialization.LenientDateTimeOffsetConverter());");
    await Assert.That(code).Contains("if (type == typeof(decimal)) return JsonMetadataServices.CreateValueInfo<decimal>(options, JsonMetadataServices.DecimalConverter);");
  }

  /// <summary>
  /// Tests that GetTypeInfoInternal includes handling for nullable primitive types like Guid?, int?.
  /// This is critical for serialization when STJ calls GetTypeInfo(typeof(Guid?)) during envelope serialization.
  /// Nullable primitives create the underlying type info first, then wrap with nullable converter.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesNullablePrimitiveTypeHandlingInGetTypeInfoInternalAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;

namespace MyApp.Commands;

public record ProcessOrder(System.Guid? OptionalId, int? OptionalQuantity) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate nullable primitive type handling that creates underlying type first, then wraps
    await Assert.That(code).Contains("if (type == typeof(int?)) { var u = JsonMetadataServices.CreateValueInfo<int>(options, JsonMetadataServices.Int32Converter); return JsonMetadataServices.CreateValueInfo<int?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    await Assert.That(code).Contains("if (type == typeof(Guid?)) { var u = JsonMetadataServices.CreateValueInfo<Guid>(options, JsonMetadataServices.GuidConverter); return JsonMetadataServices.CreateValueInfo<Guid?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    await Assert.That(code).Contains("if (type == typeof(long?)) { var u = JsonMetadataServices.CreateValueInfo<long>(options, JsonMetadataServices.Int64Converter); return JsonMetadataServices.CreateValueInfo<long?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    await Assert.That(code).Contains("if (type == typeof(bool?)) { var u = JsonMetadataServices.CreateValueInfo<bool>(options, JsonMetadataServices.BooleanConverter); return JsonMetadataServices.CreateValueInfo<bool?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    await Assert.That(code).Contains("if (type == typeof(DateTime?)) { var u = JsonMetadataServices.CreateValueInfo<DateTime>(options, JsonMetadataServices.DateTimeConverter); return JsonMetadataServices.CreateValueInfo<DateTime?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    await Assert.That(code).Contains("if (type == typeof(DateTimeOffset?)) { var u = JsonMetadataServices.CreateValueInfo<DateTimeOffset>(options, JsonMetadataServices.DateTimeOffsetConverter); return JsonMetadataServices.CreateValueInfo<DateTimeOffset?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    await Assert.That(code).Contains("if (type == typeof(decimal?)) { var u = JsonMetadataServices.CreateValueInfo<decimal>(options, JsonMetadataServices.DecimalConverter); return JsonMetadataServices.CreateValueInfo<decimal?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
  }

  /// <summary>
  /// Tests that GetTypeInfoInternal includes handling for List&lt;primitive&gt; types like List&lt;string&gt;.
  /// This is critical for nested collections like List&lt;List&lt;string&gt;&gt; - when STJ creates the
  /// outer list, it needs JsonTypeInfo for List&lt;string&gt; as the element type.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesListOfPrimitiveTypeHandlingInGetTypeInfoInternalAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;

namespace MyApp.Commands;

public record ProcessData(string Name) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate List<primitive> handling in GetTypeInfoInternal
    // These enable nested collections like List<List<string>> to work
    await Assert.That(code).Contains("if (type == typeof(global::System.Collections.Generic.List<string>))");
    await Assert.That(code).Contains("if (type == typeof(global::System.Collections.Generic.List<int>))");
    await Assert.That(code).Contains("if (type == typeof(global::System.Collections.Generic.List<long>))");
    await Assert.That(code).Contains("if (type == typeof(global::System.Collections.Generic.List<bool>))");
    await Assert.That(code).Contains("if (type == typeof(global::System.Collections.Generic.List<Guid>))");
    await Assert.That(code).Contains("if (type == typeof(global::System.Collections.Generic.List<DateTime>))");
    await Assert.That(code).Contains("if (type == typeof(global::System.Collections.Generic.List<decimal>))");

    // Should use JsonMetadataServices.CreateListInfo (not GetOrCreateTypeInfo to avoid false circular reference)
    await Assert.That(code).Contains("JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<string>, string>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ImplementsIJsonTypeInfoResolverAsync() {
    // Arrange
    const string source = @"
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
    await Assert.That(code).Contains("IJsonTypeInfoResolver");
    await Assert.That(code).Contains("GetTypeInfo(Type type, JsonSerializerOptions options)");
    await Assert.That(code).Contains("GetTypeInfoInternal(type, options)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReportsDiagnostic_ForDiscoveredMessageTypeAsync() {
    // Arrange
    const string source = @"
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
    const string source = @"
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
    await Assert.That(code).Contains("public partial class MessageJsonContext");
    await Assert.That(code).Contains("MessageId");
    await Assert.That(code).Contains("CorrelationId");

    // Should NOT contain any user message types
    await Assert.That(code).DoesNotContain("MyApp");

    // Should ALWAYS generate WhizbangJsonContext facade (even with no messages)
    var facadeCode = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(facadeCode).IsNotNull();
    await Assert.That(facadeCode).Contains("public class WhizbangJsonContext : JsonSerializerContext, IJsonTypeInfoResolver");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNestedNamespaces_GeneratesFullyQualifiedNamesAsync() {
    // Arrange
    const string source = @"
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
    await Assert.That(code).Contains("global::MyCompany.MyApp.Commands.Orders.CreateOrder");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNonMessageType_SkipsAsync() {
    // Arrange - Type with BaseList but not implementing ICommand or IEvent
    const string source = @"
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
    await Assert.That(code).DoesNotContain("OrderDto");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithEmptyProject_GeneratesEmptyContextAsync() {
    // Arrange - No message types at all
    const string source = @"
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
    await Assert.That(code).Contains("public partial class MessageJsonContext");
    await Assert.That(code).Contains("MessageId"); // Core type should be present

    // Should ALWAYS generate WhizbangJsonContext facade (even with no messages)
    var facadeCode = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(facadeCode).IsNotNull();
    await Assert.That(facadeCode).Contains("public class WhizbangJsonContext : JsonSerializerContext, IJsonTypeInfoResolver");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageJsonContextGenerator_TypeImplementingBothInterfaces_GeneratesAsCommandAsync() {
    // Arrange - Tests line 53-57, 84: Both isCommand and isEvent = true
    // messageKind ternary chooses "command" when both are true
    const string source = """
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
    await Assert.That(code).Contains("HybridMessage");

    // Check diagnostic reports it as command (not event) when both interfaces present
    var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ011");
    await Assert.That(diagnostic).IsNotNull();
    await Assert.That(diagnostic!.GetMessage(CultureInfo.InvariantCulture)).Contains("command");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageJsonContextGenerator_ClassImplementingICommand_GeneratesJsonTypeInfoAsync() {
    // Arrange - Tests line 46-50: Class (not record) in switch expression
    const string source = """
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
    await Assert.That(code).Contains("LegacyCommand");

    // Check diagnostic reports it as command
    var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ011");
    await Assert.That(diagnostic).IsNotNull();
    await Assert.That(diagnostic!.GetMessage(CultureInfo.InvariantCulture)).Contains("command");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NoMessageTypes_ReportsDiagnosticWithZeroCountAsync() {
    // Arrange - No ICommand or IEvent types
    const string source = """
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
    const string source = """
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
    await Assert.That(code).Contains("MultiPropertyCommand");
    await Assert.That(code).Contains("(bool)args[2]"); // Last property without trailing comma
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_InternalCommand_SkipsNonPublicTypeAsync() {
    // Arrange - Tests line 54: DeclaredAccessibility != Public check
    // Internal types should be skipped as generated code can't access them
    const string source = """
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
    await Assert.That(code).Contains("PublicCommand");
    await Assert.That(code).DoesNotContain("InternalCommand");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithNestedCustomType_DiscoversAndGeneratesForBothAsync() {
    // Arrange - Tests nested type discovery (lines 599-670)
    // Message with List<OrderLineItem> where OrderLineItem is a custom type
    const string source = """
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
    await Assert.That(code).Contains("CreateOrder");
    await Assert.That(code).Contains("OrderLineItem");  // Nested type discovered
    await Assert.That(code).Contains("List<global::MyApp.OrderLineItem>");  // List<T> type generated
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithPrimitiveListProperty_SkipsNestedTypeDiscoveryAsync() {
    // Arrange - Tests line 623-625: IsPrimitiveOrFrameworkType check
    // List<string> should not trigger nested type discovery (string is primitive)
    const string source = """
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
    await Assert.That(code).Contains("CreateOrder");
    // Generator skips List<primitive> generation - handled by framework
    await Assert.That(code).DoesNotContain("_List_String");  // No List<string> lazy field
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithInternalNestedType_IncludesReferenceButSkipsFactoryAsync() {
    // Arrange - Tests line 634-636: Skip factory generation for non-public nested types
    // Note: Internal types may still appear in List<T> type references but won't have factories generated
    const string source = """
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
    await Assert.That(code).Contains("CreateOrder");
    await Assert.That(code).Contains("PublicDetail");
    // PublicDetail should have factory method (uses unique identifier from FQN)
    await Assert.That(code).Contains("Create_MyApp_PublicDetail");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSameSimpleNameInDifferentNamespaces_GeneratesUniqueIdentifiersAsync() {
    // Arrange - Two types with same SimpleName but different namespaces
    // This would previously cause duplicate field names (_StartCommand) and factory methods
    const string source = """
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
    await Assert.That(code).Contains("global::MyApp.Commands.StartCommand");
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
    const string source = """
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
    await Assert.That(code).Contains("BlueprintCreatedEvent");
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
    const string source = """
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
    await Assert.That(code).Contains("NodeA");
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
    const string source = """
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
    await Assert.That(code).Contains("TreeNode");
  }

  /// <summary>
  /// Tests primitive collection skip: List&lt;string&gt;, List&lt;int&gt; should not trigger nested discovery.
  /// Also tests that custom nested types ARE discovered through the recursion.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithMixedNestedAndPrimitiveCollections_SkipsPrimitivesAndDiscoversNestedAsync() {
    // Arrange - Mix of List<CustomType> and List<string>
    const string source = """
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
    await Assert.That(code).Contains("CustomItem");
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
    const string source = """
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
    await Assert.That(code).Contains("Create_TestApp_PublicWrapper");

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
    const string source = """
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
    await Assert.That(code).Contains("SimpleEvent");
  }

  /// <summary>
  /// Tests deduplication: multiple events using the same nested type.
  /// SharedItem should have exactly one factory method generated (deduplicated).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleEventsWithSameNestedType_DeduplicatesCorrectlyAsync() {
    // Arrange - Two events using the same nested type
    const string source = """
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
    await Assert.That(code).Contains("JsonTypeInfo<global::TestApp.SharedItem> Create_TestApp_SharedItem(JsonSerializerOptions options)");

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
    const string source = """
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
    await Assert.That(code).Contains("ParentEvent");
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
    const string source = """
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
    await Assert.That(code).Contains("TopMessage");
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
    const string source = """
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
    await Assert.That(code).Contains("MixedEvent");
    await Assert.That(code).Contains("CollectionItem");  // From List<T>
    await Assert.That(code).Contains("DirectItem");      // From direct property
  }

  /// <summary>
  /// Tests that sibling nested types are discovered correctly.
  /// When a nested type (e.g., Container.Model) has a property of another nested type
  /// within the same container (e.g., Container.NestedItem), both should be discovered.
  /// This tests the GetTypeByMetadataName fix for nested types using '+' separator.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_tryGetPublicTypeSymbol</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSiblingNestedTypes_DiscoversBothTypesAsync() {
    // Arrange - Container class with two nested types: Model (ICommand) and NestedItem (used by Model)
    // This mirrors the real-world scenario: ActiveSessions.ActiveSessionsModel with List<ActiveSessions.Tab>
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public static class Container {
    // Nested type used by Model
    public record NestedItem {
        public string Name { get; init; } = "";
        public int Value { get; init; }
    }

    // Nested type that references sibling nested type
    public record Model : ICommand {
        public string Id { get; init; } = "";
        public List<NestedItem> Items { get; init; } = [];
    }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Both Model AND NestedItem should be discovered
    // NestedItem has metadata name "TestApp.Container+NestedItem" which requires special handling
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("Container.Model");    // The ICommand nested type
    await Assert.That(code).Contains("Container.NestedItem"); // The sibling nested type used in List<>
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
    const string source = """
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
    await Assert.That(code).DoesNotContain("GetOnlyModel)obj).Value = ");
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
    const string source = """
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
    await Assert.That(code).Contains("NestedStruct");
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
    const string source = """
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
    await Assert.That(code).Contains("new global::TestApp.PermissionValue(");
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
    const string source = """
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
    await Assert.That(code).Contains("CustomItem");

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
    const string source = """
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
    await Assert.That(code).DoesNotContain("HasFiles = (bool)args");

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
    const string source = """
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
    await Assert.That(code).DoesNotContain("new global::TestApp.AbstractFieldSettings()");
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
    const string source = """
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
    await Assert.That(code).Contains("OrderStatus");
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
    const string source = """
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
    await Assert.That(code).Contains("StageBlueprint");

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
    const string source = """
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
    await Assert.That(code).Contains("Stage");
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
    const string source = """
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
    await Assert.That(code).DoesNotContain("Create_TestApp_InternalStatus");
  }

  /// <summary>
  /// Tests that framework enums (like DayOfWeek) are not discovered.
  /// STJ handles these natively.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_FrameworkEnum_SkipsEnumAsync() {
    // Arrange - Event with System.DayOfWeek property
    const string source = """
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
    await Assert.That(code).DoesNotContain("Create_System_DayOfWeek");
  }

  /// <summary>
  /// Tests multiple enums in the same nested type.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleEnumsInNestedType_DiscoversAllEnumsAsync() {
    // Arrange - Nested type with multiple enum properties
    const string source = """
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
    await Assert.That(code).Contains("Priority");
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
    const string source = """
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
    await Assert.That(code).Contains("MyApp.AuthContracts+LoginCommand, TestAssembly");

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
    const string source = """
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
    await Assert.That(code).Contains("MyApp.Commands.CreateOrder, TestAssembly");

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
    const string source = """
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
    await Assert.That(code).Contains("MyApp.Outer+Inner+DeepCommand, TestAssembly");

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
    const string source = """
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
    await Assert.That(code).Contains("\"MyApp.Contracts+TestCommand, TestAssembly\"");
  }

  /// <summary>
  /// Tests that nested type uses CLR format in MessageEnvelope registration.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNestedType_GeneratesCorrectEnvelopeRegistrationAsync() {
    // Arrange - nested message type
    const string source = """
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
    await Assert.That(code).Contains("MessageEnvelope`1[[MyApp.Events+OrderCreated, TestAssembly]]");

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
    const string source = """
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
    await Assert.That(code).Contains("GlobalCommand, TestAssembly");
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
    const string source = """
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
    await Assert.That(code).Contains("CreateProductCommand");

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
    const string source = """
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
    await Assert.That(code).Contains("CreateOrderCommand");

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
    const string source = """
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
    await Assert.That(code).Contains("ProcessItemsCommand");

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
    const string source = """
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
    await Assert.That(code).Contains("LocationCommand");

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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.Guid?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.Int32?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.DateTime?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.Decimal?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.DateTimeOffset?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.Boolean?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.Int64?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.Int16?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.Byte?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.Single?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.Double?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.Char?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.Guid?>");
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
    const string source = """
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
    await Assert.That(code).DoesNotContain("CreateList_System_Collections_Generic_List");
  }

  /// <summary>
  /// Tests that List&lt;uint?&gt; is generated correctly.
  /// Generator normalizes 'uint' keyword alias to 'System.UInt32' for consistent naming.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithListOfNullableUInt_GeneratesListFactoryAsync() {
    // Arrange - Message with List<uint?> property
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.UInt32?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.UInt64?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.UInt16?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.SByte?>");
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
    const string source = """
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
    await Assert.That(code).Contains("List<global::System.Guid?>");
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
    const string source = """
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
    await Assert.That(code).Contains("\"StreamId\"");
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
    const string source = """
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
    const string source = """
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
    await Assert.That(code).Contains("\"GrandparentId\"");
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
    const string source = """
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
    const string source = """
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
    const string source = """
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
    await Assert.That(code).DoesNotContain("\"StaticProp\"");
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
    const string source = """
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
    await Assert.That(code).Contains("\"PublicProp\"");
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
    const string source = """
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
    await Assert.That(code).Contains("\"ReadOnlyProp\"");
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
    const string source = """
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
    await Assert.That(code).Contains("\"Id\"");
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
    const string source = """
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
    await Assert.That(code).Contains("\"EventId\"");
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
    const string source = """
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
    await Assert.That(code).Contains("\"Prop\"");
    // System.Object doesn't have public serializable properties, but just verify no weird ones
    await Assert.That(code).DoesNotContain("\"GetType\"");
    await Assert.That(code).DoesNotContain("\"GetHashCode\"");
  }

  // ==================== Polymorphic Type Discovery Tests ====================

  /// <summary>
  /// Tests that when a message property has an abstract type with [JsonPolymorphic],
  /// the generator discovers concrete derived types and generates JsonTypeInfo for them.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithJsonPolymorphicAbstractType_DiscoversDerivedTypesAsync() {
    // Arrange - Message with polymorphic abstract property
    const string source = """
using Whizbang.Core;
using System.Text.Json.Serialization;

namespace TestApp;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TextFieldSettings), "text")]
[JsonDerivedType(typeof(NumberFieldSettings), "number")]
public abstract record AbstractFieldSettings {
  public string Name { get; init; } = "";
}

public record TextFieldSettings : AbstractFieldSettings {
  public int MaxLength { get; init; }
}

public record NumberFieldSettings : AbstractFieldSettings {
  public decimal MinValue { get; init; }
  public decimal MaxValue { get; init; }
}

public record FormField : ICommand {
  public string FieldId { get; init; } = "";
  public AbstractFieldSettings Settings { get; init; } = null!;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate factory methods for concrete derived types
    await Assert.That(code).Contains("Create_TestApp_TextFieldSettings");
    await Assert.That(code).Contains("Create_TestApp_NumberFieldSettings");

    // Should NOT try to create factory for abstract base type
    await Assert.That(code).DoesNotContain("new global::TestApp.AbstractFieldSettings()");

    // Should generate polymorphic factory for the abstract base type
    // This is critical for deserialization - STJ needs JsonTypeInfo for the base type to dispatch to derived types
    await Assert.That(code).Contains("CreatePolymorphic_TestApp_AbstractFieldSettings");

    // Should register derived types in the polymorphic factory
    await Assert.That(code).Contains("typeof(global::TestApp.TextFieldSettings)");
    await Assert.That(code).Contains("typeof(global::TestApp.NumberFieldSettings)");
  }

  /// <summary>
  /// Tests that derived types are discovered even from [JsonDerivedType] attributes
  /// without being directly referenced in message properties.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverDerivedTypesFromAttributes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithJsonDerivedTypeAttributes_DiscoversDerivedTypesAsync() {
    // Arrange - Derived types only listed in attributes, not used directly
    const string source = """
using Whizbang.Core;
using System.Text.Json.Serialization;

namespace TestApp;

[JsonPolymorphic]
[JsonDerivedType(typeof(ConcreteSetting1))]
[JsonDerivedType(typeof(ConcreteSetting2))]
public abstract class BaseSetting {
  public string Id { get; init; } = "";
}

public class ConcreteSetting1 : BaseSetting {
  public string Value1 { get; init; } = "";
}

public class ConcreteSetting2 : BaseSetting {
  public int Value2 { get; init; }
}

public record ConfigCommand : ICommand {
  public BaseSetting Setting { get; init; } = null!;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should discover and generate for derived types from attributes
    await Assert.That(code).Contains("Create_TestApp_ConcreteSetting1");
    await Assert.That(code).Contains("Create_TestApp_ConcreteSetting2");
  }

  /// <summary>
  /// Tests that derived types in different namespace are correctly discovered
  /// when listed in [JsonDerivedType] attributes.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverDerivedTypesFromAttributes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithJsonDerivedTypeInDifferentNamespace_DiscoversAsync() {
    // Arrange - Derived type in different namespace
    const string source = """
using Whizbang.Core;
using System.Text.Json.Serialization;
using TestApp.Settings;

namespace TestApp;

[JsonPolymorphic]
[JsonDerivedType(typeof(TestApp.Settings.AdvancedSetting))]
public abstract class BaseConfig {
  public string Name { get; init; } = "";
}

namespace TestApp.Settings;

public class AdvancedSetting : BaseConfig {
  public bool Enabled { get; init; }
}

namespace TestApp;

public record SetupCommand : ICommand {
  public BaseConfig Config { get; init; } = null!;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should discover derived type from different namespace
    await Assert.That(code).Contains("TestApp_Settings_AdvancedSetting");
  }

  /// <summary>
  /// Tests that polymorphic types used in collections are correctly handled.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithJsonPolymorphicInCollection_DiscoversDerivedTypesAsync() {
    // Arrange - Polymorphic type used in a List
    const string source = """
using Whizbang.Core;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace TestApp;

[JsonPolymorphic]
[JsonDerivedType(typeof(TextField))]
[JsonDerivedType(typeof(CheckboxField))]
public abstract record FormFieldBase {
  public string Label { get; init; } = "";
}

public record TextField : FormFieldBase {
  public string Placeholder { get; init; } = "";
}

public record CheckboxField : FormFieldBase {
  public bool DefaultChecked { get; init; }
}

public record CreateFormCommand : ICommand {
  public string FormName { get; init; } = "";
  public List<FormFieldBase> Fields { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should discover derived types from polymorphic base in collection
    await Assert.That(code).Contains("Create_TestApp_TextField");
    await Assert.That(code).Contains("Create_TestApp_CheckboxField");
  }

  /// <summary>
  /// Tests diagnostic reporting when [JsonPolymorphic] types are discovered.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_generateWhizbangJsonContext</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithJsonPolymorphicType_ReportsDiagnosticForDerivedTypesAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;
using System.Text.Json.Serialization;

namespace TestApp;

[JsonPolymorphic]
[JsonDerivedType(typeof(DerivedA))]
public abstract class PolymorphicBase { }

public class DerivedA : PolymorphicBase { }

public record TestCommand : ICommand {
  public PolymorphicBase Item { get; init; } = null!;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should report discovery of derived types
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Should report diagnostic for discovered derived type (WHIZ011 is JsonSerializableTypeDiscovered)
    var discoveryDiagnostics = result.Diagnostics
        .Where(d => d.Id == "WHIZ011" && d.GetMessage(CultureInfo.InvariantCulture).Contains("DerivedA"))
        .ToList();
    await Assert.That(discoveryDiagnostics.Count).IsGreaterThanOrEqualTo(1);
  }

  /// <summary>
  /// Tests that enum types automatically generate both non-nullable and nullable JsonTypeInfo factories.
  /// This ensures that when an enum is discovered, we can serialize both EnumType and EnumType?.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EnumProperty_GeneratesNullableEnumFactoryAsync() {
    // Arrange - Event with enum property (discovered enums should get nullable factory too)
    const string source = """
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

    // Assert - Should not have errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should have both non-nullable and nullable enum handling
    // Non-nullable: CreateEnum_TestApp_OrderStatus
    await Assert.That(code).Contains("CreateEnum_TestApp_OrderStatus");
    await Assert.That(code).Contains("GetEnumConverter<global::TestApp.OrderStatus>");

    // Nullable: CreateNullableEnum_TestApp_OrderStatus
    await Assert.That(code).Contains("CreateNullableEnum_TestApp_OrderStatus");
    await Assert.That(code).Contains("GetNullableConverter<global::TestApp.OrderStatus>");

    // Should have GetTypeInfo checks for both
    await Assert.That(code).Contains("if (type == typeof(global::TestApp.OrderStatus))");
    await Assert.That(code).Contains("if (type == typeof(global::TestApp.OrderStatus?))");
  }

  /// <summary>
  /// Tests that nullable enum property in source code works with auto-generated nullable factory.
  /// Even if the source has OrderStatus? directly, the generator creates factories for both.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NullableEnumProperty_GeneratesBothFactoriesAsync() {
    // Arrange - Event with nullable enum property
    const string source = """
using Whizbang.Core;

namespace TestApp;

public enum MessageFlags { None, Important, Urgent, Archived }

public record MessageUpdatedEvent : IEvent {
    public string MessageId { get; init; } = "";
    public MessageFlags? Flags { get; init; }  // Nullable enum property
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should not have errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should have both non-nullable and nullable enum handling
    // Even though source only has MessageFlags?, both are generated
    await Assert.That(code).Contains("CreateEnum_TestApp_MessageFlags");
    await Assert.That(code).Contains("CreateNullableEnum_TestApp_MessageFlags");

    // Should have GetTypeInfo checks for both
    await Assert.That(code).Contains("if (type == typeof(global::TestApp.MessageFlags))");
    await Assert.That(code).Contains("if (type == typeof(global::TestApp.MessageFlags?))");
  }

  /// <summary>
  /// Tests that nested perspective models are discovered when the containing type
  /// implements IPerspectiveFor with the nested model as TModel.
  /// This is the common pattern: ChatSession implements IPerspectiveFor&lt;ChatSessionModel&gt;
  /// where ChatSessionModel is nested inside ChatSession.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NestedPerspectiveModel_IsDiscoveredAsync() {
    // Arrange - Perspective with nested model (the ChatSession pattern)
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestApp;

public class ChatSession : IPerspectiveFor<ChatSession.ChatSessionModel, ChatSession.MessageSent> {
    public record ChatSessionModel {
        public string SessionId { get; init; } = "";
        public string Title { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
    }

    public record MessageSent : IEvent {
        public string SessionId { get; init; } = "";
        public string Content { get; init; } = "";
    }

    public ChatSessionModel Apply(ChatSessionModel model, MessageSent e) => model;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should not have errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // The nested model should be discovered because ChatSession implements IPerspectiveFor<ChatSessionModel, ...>
    // Check for the nested type using CLR format (+ for nested types)
    await Assert.That(code).Contains("ChatSession");
    await Assert.That(code).Contains("ChatSessionModel");

    // Should have factory method for the nested model
    await Assert.That(code).Contains("Create_TestApp_ChatSession_ChatSessionModel");
  }

  /// <summary>
  /// Tests that a nested perspective model discovered through both the model record path
  /// (syntactic predicate matches nested type) AND the perspective class path
  /// (_extractPerspectiveModelFromPerspectiveClass) is only registered once.
  /// Regression test for CS0111 duplicate method errors.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NestedPerspectiveModel_IsNotDuplicatedAsync() {
    // Arrange - Perspective with nested model (triggers both discovery paths)
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestApp;

public class ChatSession : IPerspectiveFor<ChatSession.ChatSessionModel, ChatSession.MessageSent> {
    public record ChatSessionModel {
        public string SessionId { get; init; } = "";
        public string Title { get; init; } = "";
    }

    public record MessageSent : IEvent {
        public string SessionId { get; init; } = "";
    }

    public ChatSessionModel Apply(ChatSessionModel model, MessageSent e) => model;
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No generator errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Model should be present
    await Assert.That(code).Contains("Create_TestApp_ChatSession_ChatSessionModel");

    // Count factory method definitions — must be exactly 1 (not duplicated)
    var factoryCount = code!.Split("Create_TestApp_ChatSession_ChatSessionModel(JsonSerializerOptions options)").Length - 1;
    await Assert.That(factoryCount).IsEqualTo(1)
      .Because("perspective model should not be registered twice via both the nested type path and the perspective class extraction path");
  }

  /// <summary>
  /// Tests that types with [WhizbangSerializable] attribute are discovered even without base types.
  /// This covers scenarios like DTOs that need JSON serialization but aren't messages.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_TypeWithWhizbangSerializableAttribute_IsDiscoveredAsync() {
    // Arrange - Type with [WhizbangSerializable] attribute (no base type)
    const string source = """
using Whizbang;

namespace TestApp;

[WhizbangSerializable]
public record ChatMessageDto {
    public string Id { get; init; } = "";
    public string Content { get; init; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should not have errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Type with [WhizbangSerializable] should be discovered for JSON serialization
    await Assert.That(code).Contains("ChatMessageDto");
  }

  // ==================== Array Type Discovery Tests ====================

  /// <summary>
  /// Tests that array types (T[]) are discovered from message properties.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverArrayTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithArrayProperty_DiscoversArrayTypeAsync() {
    // Arrange - Message with string[] property
    const string source = """
using Whizbang.Core;

namespace TestApp;

public record ProcessTagsCommand(string[] Tags) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Array type should be discovered and factory generated
    await Assert.That(code).Contains("global::System.String[]");
    await Assert.That(code).Contains("CreateArray_System_String");
  }

  /// <summary>
  /// Tests that array types with nullable element types are handled correctly.
  /// E.g., int?[] should generate a factory with proper element type handling.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverArrayTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithNullableElementArray_GeneratesArrayFactoryAsync() {
    // Arrange - Message with int?[] property
    const string source = """
using Whizbang.Core;

namespace TestApp;

public record ProcessValuesCommand(int?[] OptionalValues) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Array of nullable int should be discovered
    // Generator normalizes 'int' -> 'global::System.Int32' for consistent naming
    await Assert.That(code).Contains("global::System.Int32?[]");
    await Assert.That(code).Contains("CreateArray_System_Int32__Nullable");
  }

  /// <summary>
  /// Tests that array types with custom element types are discovered.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverArrayTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithCustomTypeArray_GeneratesArrayFactoryAsync() {
    // Arrange - Message with custom type array
    const string source = """
using Whizbang.Core;

namespace TestApp;

public record OrderItem {
    public string ProductId { get; init; } = "";
    public int Quantity { get; init; }
}

public record CreateOrderCommand(OrderItem[] Items) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Array of custom type should be discovered
    await Assert.That(code).Contains("global::TestApp.OrderItem[]");
    await Assert.That(code).Contains("CreateArray_TestApp_OrderItem");
  }

  /// <summary>
  /// Tests that Guid[] arrays are properly discovered and generated.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverArrayTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithGuidArray_GeneratesArrayFactoryAsync() {
    // Arrange - Message with Guid[] property
    const string source = """
using Whizbang.Core;
using System;

namespace TestApp;

public record ProcessIdsCommand(Guid[] Ids) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Guid[] should be discovered
    await Assert.That(code).Contains("global::System.Guid[]");
    await Assert.That(code).Contains("CreateArray_System_Guid");
  }

  /// <summary>
  /// Tests that the generator handles arrays of generic types like Dictionary&lt;string, string&gt;[].
  /// The generator must sanitize angle brackets and commas from the type name to create valid C# identifiers.
  /// </summary>
  /// <tests>src/Whizbang.Generators/ArrayTypeInfo.cs:ElementUniqueIdentifier</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithGenericTypeArray_GeneratesValidIdentifierAsync() {
    // Arrange - Message with Dictionary<string, string>[] property
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ProcessMetadataCommand(Dictionary<string, string>[] Metadata) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors (this was failing before the fix)
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Dictionary<string, string>[] should generate valid identifier (no < > , in method names)
    await Assert.That(code).Contains("CreateArray_System_Collections_Generic_Dictionary");
    // Should NOT contain angle brackets in method names
    await Assert.That(code).DoesNotContain("CreateArray_System_Collections_Generic_Dictionary<");
  }

  /// <summary>
  /// Tests that the generator properly handles TimeSpan and TimeSpan? properties.
  /// TimeSpan is listed in _isPrimitiveOrFrameworkType (skipped from discovery),
  /// so GetOrCreateTypeInfo must have explicit handling for it.
  /// This test would have caught the regression where TimeSpan was in the skip list
  /// but not in GetOrCreateTypeInfo.
  /// </summary>
  /// <tests>src/Whizbang.Generators/Templates/Snippets/JsonContextSnippets.cs:HELPER_GET_OR_CREATE_TYPE_INFO</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithTimeSpanProperty_GeneratesValidCodeAsync() {
    // Arrange - Message with TimeSpan and TimeSpan? properties
    const string source = """
using Whizbang.Core;
using System;

namespace TestApp;

public record ScheduleCommand(TimeSpan Duration, TimeSpan? OptionalDelay) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // TimeSpan should be handled in GetOrCreateTypeInfo (not discovered as a nested type)
    await Assert.That(code).Contains("typeof(TimeSpan)");
  }

  /// <summary>
  /// Tests that the generator properly handles DateOnly and DateOnly? properties.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithDateOnlyProperty_GeneratesValidCodeAsync() {
    // Arrange - Message with DateOnly and DateOnly? properties
    const string source = """
using Whizbang.Core;
using System;

namespace TestApp;

public record AppointmentCommand(DateOnly Date, DateOnly? OptionalDate) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // DateOnly should be handled in GetOrCreateTypeInfo
    await Assert.That(code).Contains("typeof(DateOnly)");
  }

  /// <summary>
  /// Tests that the generator properly handles TimeOnly and TimeOnly? properties.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithTimeOnlyProperty_GeneratesValidCodeAsync() {
    // Arrange - Message with TimeOnly and TimeOnly? properties
    const string source = """
using Whizbang.Core;
using System;

namespace TestApp;

public record MeetingCommand(TimeOnly StartTime, TimeOnly? EndTime) : ICommand;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // TimeOnly should be handled in GetOrCreateTypeInfo
    await Assert.That(code).Contains("typeof(TimeOnly)");
  }

  /// <summary>
  /// Tests that recursive property type discovery correctly handles framework types
  /// like TimeSpan? in deeply nested types. This is the regression test for the bug
  /// where a perspective model's nested type had a TimeSpan? property that wasn't
  /// being properly handled because:
  /// 1. The nested type (RecordedFact) was discovered recursively
  /// 2. Its TimeSpan? property was skipped (correctly) by _isPrimitiveOrFrameworkType
  /// 3. But GetOrCreateTypeInfo didn't have TimeSpan handling, causing runtime failure
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NestedTypeWithTimeSpanProperty_GeneratesValidCodeAsync() {
    // Arrange - Message with nested type that has TimeSpan? property
    // This simulates the real-world scenario where IntentModel contains
    // List<RecordedFact> and RecordedFact has a TimeSpan? property
    const string source = """
using Whizbang.Core;
using System;
using System.Collections.Generic;

namespace TestApp;

public record RecordedFact(string Name, TimeSpan? Duration);

public record IntentModel(List<RecordedFact> Facts);

public record IntentUpdated(IntentModel Model) : IEvent;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors (this would fail before the fix)
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // RecordedFact should be discovered as a nested type
    await Assert.That(code).Contains("RecordedFact");

    // The generated code should handle TimeSpan via GetOrCreateTypeInfo
    await Assert.That(code).Contains("typeof(TimeSpan)");
  }

  /// <summary>
  /// Tests that List of nested types with framework type properties works correctly.
  /// This tests the combination of List discovery + nested type discovery + TimeSpan handling.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ListOfNestedTypeWithTimeSpanProperty_GeneratesValidCodeAsync() {
    // Arrange - More complex nested structure
    const string source = """
using Whizbang.Core;
using System;
using System.Collections.Generic;

namespace TestApp;

public record ScheduleItem(string Title, TimeSpan Duration, DateOnly? ScheduledDate, TimeOnly? StartTime);

public record DaySchedule(DateOnly Date, List<ScheduleItem> Items);

public record ScheduleCreated(List<DaySchedule> Schedules) : IEvent;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Both nested types should be discovered
    await Assert.That(code).Contains("ScheduleItem");
    await Assert.That(code).Contains("DaySchedule");

    // List types should be discovered
    await Assert.That(code).Contains("List<global::TestApp.ScheduleItem>");
    await Assert.That(code).Contains("List<global::TestApp.DaySchedule>");
  }

  /// <summary>
  /// Tests that recursive discovery handles repeat types correctly.
  /// The same nested type appears in multiple places in the type graph.
  /// The generator should discover it once and not create duplicates.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RecursiveDiscovery_WithRepeatTypes_DiscoversOnceAsync() {
    // Arrange - Same type (Address) appears in multiple properties and nested types
    const string source = """
using Whizbang.Core;
using System;
using System.Collections.Generic;

namespace TestApp;

public record Address(string Street, string City, TimeSpan? DeliveryWindow);

public record Customer(string Name, Address BillingAddress, Address? ShippingAddress);

public record Order(Customer Customer, Address DeliveryAddress, List<Address> AlternateAddresses);

public record OrderCreated(Order Order) : IEvent;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // All nested types should be discovered
    await Assert.That(code).Contains("Address");
    await Assert.That(code).Contains("Customer");
    await Assert.That(code).Contains("Order");

    // Address should only have ONE factory method definition (not duplicates)
    // Use pattern that matches method definition, not calls
    var addressFactoryCount = System.Text.RegularExpressions.Regex.Count(
        code, @"private JsonTypeInfo<[^>]+> Create_TestApp_Address\(");
    await Assert.That(addressFactoryCount).IsEqualTo(1);
  }

  /// <summary>
  /// Tests that recursive discovery handles circular references correctly.
  /// Type A references Type B, and Type B references Type A.
  /// The generator should not infinite loop and should discover both types once.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RecursiveDiscovery_WithCircularReferences_HandlesGracefullyAsync() {
    // Arrange - Circular reference: Person -> List<Person> (children reference parents)
    const string source = """
using Whizbang.Core;
using System;
using System.Collections.Generic;

namespace TestApp;

public record Person(string Name, TimeSpan? WorkHours, Person? Manager, List<Person> DirectReports);

public record TeamCreated(Person TeamLead) : IEvent;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors (generator shouldn't infinite loop)
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Person should be discovered
    await Assert.That(code).Contains("Person");

    // Person should only have ONE factory method definition (not duplicates from circular traversal)
    var personFactoryCount = System.Text.RegularExpressions.Regex.Count(
        code, @"private JsonTypeInfo<[^>]+> Create_TestApp_Person\(");
    await Assert.That(personFactoryCount).IsEqualTo(1);

    // List<Person> should also be discovered
    await Assert.That(code).Contains("List<global::TestApp.Person>");
  }

  /// <summary>
  /// Tests that recursive discovery handles self-referencing types correctly.
  /// A type that directly references itself (e.g., tree node pattern).
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RecursiveDiscovery_WithSelfReference_HandlesGracefullyAsync() {
    // Arrange - Self-reference: TreeNode references itself for children
    const string source = """
using Whizbang.Core;
using System;
using System.Collections.Generic;

namespace TestApp;

public record TreeNode(string Value, TimeSpan? ProcessingTime, TreeNode? Parent, List<TreeNode> Children);

public record TreeCreated(TreeNode Root) : IEvent;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // TreeNode should be discovered once
    await Assert.That(code).Contains("TreeNode");

    var treeNodeFactoryCount = System.Text.RegularExpressions.Regex.Count(
        code, @"private JsonTypeInfo<[^>]+> Create_TestApp_TreeNode\(");
    await Assert.That(treeNodeFactoryCount).IsEqualTo(1);
  }

  /// <summary>
  /// Tests mutual circular references (A -> B -> A pattern).
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RecursiveDiscovery_WithMutualCircularReferences_HandlesGracefullyAsync() {
    // Arrange - Mutual circular: Department -> List<Employee>, Employee -> Department
    const string source = """
using Whizbang.Core;
using System;
using System.Collections.Generic;

namespace TestApp;

public record Employee(string Name, TimeSpan? ShiftDuration, Department? Department);

public record Department(string Name, Employee? Manager, List<Employee> Staff);

public record OrgCreated(Department RootDepartment) : IEvent;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Both types should be discovered once
    await Assert.That(code).Contains("Employee");
    await Assert.That(code).Contains("Department");

    var employeeFactoryCount = System.Text.RegularExpressions.Regex.Count(
        code, @"private JsonTypeInfo<[^>]+> Create_TestApp_Employee\(");
    await Assert.That(employeeFactoryCount).IsEqualTo(1);

    var departmentFactoryCount = System.Text.RegularExpressions.Regex.Count(
        code, @"private JsonTypeInfo<[^>]+> Create_TestApp_Department\(");
    await Assert.That(departmentFactoryCount).IsEqualTo(1);
  }

  /// <summary>
  /// Tests that an event containing a List of the SAME event type works correctly.
  /// This is a critical case for hierarchical events (e.g., FilterSubscriptionTemplateCreatedEvent
  /// with List&lt;FilterSubscriptionTemplateCreatedEvent&gt; Children property).
  /// Uses deferred property initialization to break the circular reference.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_generateMessageTypeFactories</tests>
  /// <tests>src/Whizbang.Generators/Templates/Snippets/JsonContextSnippets.cs:HELPER_TRY_GET_OR_CREATE_TYPE_INFO</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EventWithSelfReferencingCollection_GeneratesCorrectlyAsync() {
    // Arrange - Event with List<SameEvent> property (self-referencing collection)
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

/// <summary>
/// An event that contains a list of the same event type.
/// This represents hierarchical data like filter templates with children.
/// </summary>
public record TemplateCreatedEvent : IEvent {
  public string Name { get; init; } = "";
  public List<TemplateCreatedEvent> Children { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors (deferred initialization should handle circular reference)
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Event factory should be generated
    await Assert.That(code).Contains("Create_TestApp_TemplateCreatedEvent");

    // List<TemplateCreatedEvent> should also be generated
    await Assert.That(code).Contains("List<global::TestApp.TemplateCreatedEvent>");

    // Deferred property initialization pattern should be present
    await Assert.That(code).Contains("CreatePropertiesFor_TestApp_TemplateCreatedEvent");
    await Assert.That(code).Contains("CreateCtorParamsFor_TestApp_TemplateCreatedEvent");

    // Type info should be cached BEFORE deferred initialization runs
    await Assert.That(code).Contains("TypeInfoCache[typeof(global::TestApp.TemplateCreatedEvent)]");

    // Event should have exactly one factory method
    var factoryCount = System.Text.RegularExpressions.Regex.Count(
        code, @"private JsonTypeInfo<[^>]+> Create_TestApp_TemplateCreatedEvent\(");
    await Assert.That(factoryCount).IsEqualTo(1);
  }

  /// <summary>
  /// Tests deeply nested self-referencing hierarchy.
  /// Event -> NestedType -> List&lt;NestedType&gt;
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_generateMessageTypeFactories</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NestedTypeWithSelfReferencingCollection_GeneratesCorrectlyAsync() {
    // Arrange - NestedType with self-referencing collection
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record NestedNode {
  public string Id { get; init; } = "";
  public NestedNode? Parent { get; init; }
  public List<NestedNode> Children { get; init; } = new();
}

public record HierarchyCreatedEvent : IEvent {
  public NestedNode Root { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Both types should be generated
    await Assert.That(code).Contains("Create_TestApp_NestedNode");
    await Assert.That(code).Contains("Create_TestApp_HierarchyCreatedEvent");

    // List<NestedNode> should be generated
    await Assert.That(code).Contains("List<global::TestApp.NestedNode>");

    // Deferred initialization for NestedNode
    await Assert.That(code).Contains("CreatePropertiesFor_TestApp_NestedNode");
  }

  // ==================== Auto-Discovered Polymorphic Base Type Tests ====================
  // These tests verify automatic discovery of derived types for base classes WITHOUT
  // explicit [JsonPolymorphic] attributes. The generator should track inheritance
  // during IEvent/ICommand scanning and generate polymorphic serialization automatically.

  /// <summary>
  /// Tests that when a user-defined base class is used in a collection (List&lt;BaseEvent&gt;),
  /// the generator auto-discovers all derived event types and generates polymorphic serialization.
  /// This is the core use case - no [JsonPolymorphic] attribute required.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractInheritanceChain</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_buildPolymorphicRegistry</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithUserBaseClass_AutoDiscoversPolymorphicTypesAsync() {
    // Arrange - BaseJdxEvent-like pattern with multiple derived events
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

// User-defined base event class (no [JsonPolymorphic] attribute!)
public class BaseJdxEvent : IEvent {
  public string EventId { get; init; } = "";
}

public class SeedCreatedEvent : BaseJdxEvent {
  public string SeedId { get; init; } = "";
}

public class SeedProcessedEvent : BaseJdxEvent {
  public DateTime ProcessedAt { get; init; }
}

public class SeedCompletedEvent : BaseJdxEvent {
  public int TotalRecords { get; init; }
}

// Handler returns List<BaseJdxEvent>
public record ProcessSeedBatchCommand : ICommand {
  public List<BaseJdxEvent> Events { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate polymorphic factory for BaseJdxEvent
    await Assert.That(code).Contains("CreatePolymorphic_TestApp_BaseJdxEvent");

    // Should include JsonPolymorphismOptions with derived types
    await Assert.That(code).Contains("JsonPolymorphismOptions");
    await Assert.That(code).Contains("SeedCreatedEvent");
    await Assert.That(code).Contains("SeedProcessedEvent");
    await Assert.That(code).Contains("SeedCompletedEvent");
  }

  /// <summary>
  /// Tests that when a handler returns List&lt;IEvent&gt;, the generator includes
  /// ALL event types discovered in the compilation as derived types.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_buildPolymorphicRegistry</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithIEventCollection_IncludesAllEventTypesAsync() {
    // Arrange - Multiple events, handler returns List<IEvent>
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record OrderCreatedEvent : IEvent {
  public string OrderId { get; init; } = "";
}

public record OrderShippedEvent : IEvent {
  public string TrackingNumber { get; init; } = "";
}

public record OrderDeliveredEvent : IEvent {
  public DateTime DeliveredAt { get; init; }
}

// Command with List<IEvent> property - should trigger polymorphic serialization
public record GetEventsCommand : ICommand {
  public List<IEvent> AllEvents { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate polymorphic factory for IEvent
    await Assert.That(code).Contains("CreatePolymorphic_Whizbang_Core_IEvent");

    // Should include all discovered event types as derived
    await Assert.That(code).Contains("OrderCreatedEvent");
    await Assert.That(code).Contains("OrderShippedEvent");
    await Assert.That(code).Contains("OrderDeliveredEvent");
  }

  /// <summary>
  /// Tests that when a handler returns List&lt;ICommand&gt;, the generator includes
  /// ALL command types discovered in the compilation as derived types.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_buildPolymorphicRegistry</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithICommandCollection_IncludesAllCommandTypesAsync() {
    // Arrange - Multiple commands, handler returns List<ICommand>
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record CreateOrderCommand : ICommand {
  public string ProductId { get; init; } = "";
}

public record CancelOrderCommand : ICommand {
  public string OrderId { get; init; } = "";
}

public record UpdateOrderCommand : ICommand {
  public string OrderId { get; init; } = "";
  public int Quantity { get; init; }
}

// Result with List<ICommand> - should trigger polymorphic serialization
public record CommandBatch : IEvent {
  public List<ICommand> Commands { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate polymorphic factory for ICommand
    await Assert.That(code).Contains("CreatePolymorphic_Whizbang_Core_ICommand");

    // Should include all discovered command types as derived
    await Assert.That(code).Contains("CreateOrderCommand");
    await Assert.That(code).Contains("CancelOrderCommand");
    await Assert.That(code).Contains("UpdateOrderCommand");
  }

  /// <summary>
  /// Tests that user-defined interfaces are also tracked for polymorphic serialization.
  /// When List&lt;IMyInterface&gt; is used, all implementations should be discovered.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractInheritanceChain</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithUserInterface_AutoDiscoversImplementationsAsync() {
    // Arrange - User interface with multiple implementations
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

// User-defined interface
public interface INotification {
  string Message { get; }
}

public record EmailNotification : IEvent, INotification {
  public string Message { get; init; } = "";
  public string EmailAddress { get; init; } = "";
}

public record SmsNotification : IEvent, INotification {
  public string Message { get; init; } = "";
  public string PhoneNumber { get; init; } = "";
}

public record PushNotification : IEvent, INotification {
  public string Message { get; init; } = "";
  public string DeviceToken { get; init; } = "";
}

// Command using the interface in a list
public record SendNotificationsCommand : ICommand {
  public List<INotification> Notifications { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate polymorphic factory for INotification
    await Assert.That(code).Contains("CreatePolymorphic_TestApp_INotification");

    // Should include all implementations
    await Assert.That(code).Contains("EmailNotification");
    await Assert.That(code).Contains("SmsNotification");
    await Assert.That(code).Contains("PushNotification");
  }

  /// <summary>
  /// Tests that deep inheritance hierarchies are fully tracked.
  /// If A extends B extends C implements IEvent, then:
  /// - C should list A and B as derived
  /// - B should list A as derived
  /// - IEvent should list A, B, and C as derived
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractInheritanceChain</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithDeepInheritance_DiscoversAllLevelsAsync() {
    // Arrange - Three-level inheritance hierarchy
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

// Level 0: Base event
public class DomainEvent : IEvent {
  public Guid EventId { get; init; }
}

// Level 1: Intermediate class
public class AuditableEvent : DomainEvent {
  public string AuditInfo { get; init; } = "";
}

// Level 2: Concrete event
public class OrderAuditedEvent : AuditableEvent {
  public string OrderId { get; init; } = "";
}

// Another Level 2 branch
public class UserAuditedEvent : AuditableEvent {
  public string UserId { get; init; } = "";
}

// Command using base type in list
public record GetAuditEventsCommand : ICommand {
  public List<DomainEvent> Events { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate polymorphic factory for DomainEvent
    await Assert.That(code).Contains("CreatePolymorphic_TestApp_DomainEvent");

    // Should include all descendants (not just direct children)
    await Assert.That(code).Contains("AuditableEvent");
    await Assert.That(code).Contains("OrderAuditedEvent");
    await Assert.That(code).Contains("UserAuditedEvent");
  }

  /// <summary>
  /// Tests that when a base type HAS [JsonPolymorphic] attribute, the generator
  /// uses the user's explicit configuration instead of auto-discovering.
  /// This is the opt-out mechanism.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_buildPolymorphicRegistry</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithExplicitJsonPolymorphic_UsesUserAttributesAsync() {
    // Arrange - Base has [JsonPolymorphic] - user controls derived types
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TestApp;

// User explicitly controls polymorphism
[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventType")]
[JsonDerivedType(typeof(SelectedEvent1), "selected1")]
// Note: SelectedEvent2 is NOT listed - user chose to exclude it
public class ControlledBaseEvent : IEvent {
  public string Id { get; init; } = "";
}

public class SelectedEvent1 : ControlledBaseEvent {
  public string Data1 { get; init; } = "";
}

public class SelectedEvent2 : ControlledBaseEvent {
  public string Data2 { get; init; } = "";
}

public record GetControlledEventsCommand : ICommand {
  public List<ControlledBaseEvent> Events { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should NOT generate auto-polymorphic factory for ControlledBaseEvent
    // (user has explicit [JsonPolymorphic] so we respect their configuration)
    await Assert.That(code).DoesNotContain("CreatePolymorphic_TestApp_ControlledBaseEvent");

    // The explicit [JsonDerivedType] handling should still work
    await Assert.That(code).Contains("SelectedEvent1");
  }

  /// <summary>
  /// Tests that abstract derived types are excluded from polymorphic registration
  /// since they cannot be instantiated.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_buildPolymorphicRegistry</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithAbstractDerivedType_ExcludesItAsync() {
    // Arrange - Abstract intermediate class
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public class BaseEvent : IEvent {
  public string Id { get; init; } = "";
}

// Abstract - should NOT be included as derived type
public abstract class AbstractMiddleEvent : BaseEvent {
  public abstract string Category { get; }
}

// Concrete - should be included
public class ConcreteEvent : AbstractMiddleEvent {
  public override string Category => "concrete";
  public string Value { get; init; } = "";
}

public record GetBaseEventsCommand : ICommand {
  public List<BaseEvent> Events { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should include concrete type
    await Assert.That(code).Contains("ConcreteEvent");

    // The polymorphic registration should NOT include abstract type
    // (Check that AbstractMiddleEvent is not in DerivedTypes.Add calls)
    var polymorphicSection = code.Substring(
        code.IndexOf("CreatePolymorphic_TestApp_BaseEvent", StringComparison.Ordinal),
        Math.Min(500, code.Length - code.IndexOf("CreatePolymorphic_TestApp_BaseEvent", StringComparison.Ordinal))
    );
    await Assert.That(polymorphicSection).DoesNotContain("AbstractMiddleEvent");
  }

  /// <summary>
  /// Tests that non-public (internal) derived types are excluded from
  /// polymorphic registration.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_buildPolymorphicRegistry</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNonPublicDerivedType_ExcludesItAsync() {
    // Arrange - Internal derived type
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public class PublicBaseEvent : IEvent {
  public string Id { get; init; } = "";
}

// Public - should be included
public class PublicDerivedEvent : PublicBaseEvent {
  public string PublicData { get; init; } = "";
}

// Internal - should NOT be included
internal class InternalDerivedEvent : PublicBaseEvent {
  public string InternalData { get; init; } = "";
}

public record GetPublicEventsCommand : ICommand {
  public List<PublicBaseEvent> Events { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should include public derived type
    await Assert.That(code).Contains("PublicDerivedEvent");

    // Should NOT include internal derived type in polymorphic registration
    await Assert.That(code).DoesNotContain("InternalDerivedEvent");
  }

  /// <summary>
  /// Tests that array types (IEvent[]) also trigger polymorphic discovery,
  /// not just List&lt;T&gt;.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_buildPolymorphicRegistry</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithArrayOfBaseType_AutoDiscoversPolymorphicTypesAsync() {
    // Arrange - Array of base type
    const string source = """
using Whizbang.Core;

namespace TestApp;

public class BatchEvent : IEvent {
  public string BatchId { get; init; } = "";
}

public class StartBatchEvent : BatchEvent {
  public DateTime StartedAt { get; init; }
}

public class EndBatchEvent : BatchEvent {
  public DateTime EndedAt { get; init; }
}

// Array syntax instead of List<T>
public record ProcessBatchCommand : ICommand {
  public BatchEvent[] Events { get; init; } = [];
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate polymorphic factory for BatchEvent
    await Assert.That(code).Contains("CreatePolymorphic_TestApp_BatchEvent");

    // Should include derived types
    await Assert.That(code).Contains("StartBatchEvent");
    await Assert.That(code).Contains("EndBatchEvent");
  }

  /// <summary>
  /// Tests that a diagnostic (WHIZ071) is reported when polymorphic base types
  /// are discovered with their derived type count.
  /// </summary>
  /// <tests>src/Whizbang.Generators/DiagnosticDescriptors.cs:PolymorphicBaseTypeDiscovered</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPolymorphicBase_ReportsWHIZ071DiagnosticAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public class DiagnosticTestEvent : IEvent {
  public string Id { get; init; } = "";
}

public class DerivedEvent1 : DiagnosticTestEvent { }
public class DerivedEvent2 : DiagnosticTestEvent { }

public record TestCommand : ICommand {
  public List<DiagnosticTestEvent> Events { get; init; } = new();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should report WHIZ071 diagnostic for the discovered polymorphic base
    var whiz071Diagnostics = result.Diagnostics
        .Where(d => d.Id == "WHIZ071")
        .ToList();

    await Assert.That(whiz071Diagnostics.Count).IsGreaterThanOrEqualTo(1);

    // The diagnostic should mention DiagnosticTestEvent and count of derived types
    var diagnostic = whiz071Diagnostics.FirstOrDefault(d =>
        d.GetMessage(CultureInfo.InvariantCulture).Contains("DiagnosticTestEvent"));
    await Assert.That(diagnostic).IsNotNull();
  }

  // ============================================================================
  // Dictionary Value Type Discovery Tests
  // ============================================================================
  // Tests for _extractElementType handling of Dictionary<TKey, TValue> types.
  // The generator should extract and discover the VALUE type (TValue) for
  // AOT-compatible JSON serialization.
  // <docs>source-generators/json-contexts</docs>
  // ============================================================================

  /// <summary>
  /// Tests that Dictionary&lt;string, TValue&gt; properties have their value type discovered.
  /// This is the basic case - value type should be extracted and included in generated JsonContext.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithDictionaryProperty_DiscoversValueTypeAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record SeedSectionContext {
  public required string SectionName { get; init; }
  public required System.Guid SectionId { get; init; }
}

public record JobTemplateSeedOrchestrationInitiatedEvent : IEvent {
  public required Dictionary<string, SeedSectionContext> SectionContexts { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - SeedSectionContext should be discovered as a nested type
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Value type must be discovered and have JsonTypeInfo generated
    await Assert.That(code).Contains("SeedSectionContext");
    await Assert.That(code).Contains("Create_TestApp_SeedSectionContext");
  }

  /// <summary>
  /// Tests that deeply nested Dictionary value types are discovered recursively.
  /// When Dictionary&lt;string, ComplexType&gt; where ComplexType has its own nested types,
  /// all levels should be discovered.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithNestedDictionaryValue_DiscoversDeepTypesAsync() {
    // Arrange - Dictionary value type has its own nested types
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record InnerDetail {
  public required string Value { get; init; }
}

public record OuterConfig {
  public required string Name { get; init; }
  public required List<InnerDetail> Details { get; init; }
}

public record ConfigurationEvent : IEvent {
  public required Dictionary<string, OuterConfig> Configurations { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Both OuterConfig AND InnerDetail should be discovered
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // All nested levels must be discovered
    await Assert.That(code).Contains("OuterConfig");
    await Assert.That(code).Contains("InnerDetail");
    await Assert.That(code).Contains("Create_TestApp_OuterConfig");
    await Assert.That(code).Contains("Create_TestApp_InnerDetail");
  }

  /// <summary>
  /// Tests that IDictionary&lt;TKey, TValue&gt; interface properties have their value type discovered.
  /// Interface variants should work the same as concrete Dictionary.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithIDictionaryProperty_DiscoversValueTypeAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record MetadataValue {
  public required string Key { get; init; }
  public required object Value { get; init; }
}

public record MetadataEvent : IEvent {
  public required IDictionary<string, MetadataValue> Metadata { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code).Contains("MetadataValue");
    await Assert.That(code).Contains("Create_TestApp_MetadataValue");
  }

  /// <summary>
  /// Tests that IReadOnlyDictionary&lt;TKey, TValue&gt; properties have their value type discovered.
  /// Read-only interface variants should work the same as concrete Dictionary.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithIReadOnlyDictionaryProperty_DiscoversValueTypeAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record CacheEntry {
  public required string Data { get; init; }
  public required System.DateTime ExpiresAt { get; init; }
}

public record CacheSnapshotEvent : IEvent {
  public required IReadOnlyDictionary<string, CacheEntry> Entries { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code).Contains("CacheEntry");
    await Assert.That(code).Contains("Create_TestApp_CacheEntry");
  }

  /// <summary>
  /// Tests that Dictionary with complex key type (non-string) still extracts value type.
  /// The key type (TKey) is handled by System.Text.Json natively, we only need value type.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_findTopLevelComma</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DictionaryWithNonStringKey_DiscoversValueTypeOnlyAsync() {
    // Arrange - Dictionary<int, CustomType> - int key handled by STJ, CustomType value needs discovery
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record IndexedItem {
  public required string Name { get; init; }
  public required int Position { get; init; }
}

public record IndexedCollectionEvent : IEvent {
  public required Dictionary<int, IndexedItem> ItemsByIndex { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Value type should be discovered
    await Assert.That(code).Contains("IndexedItem");
    await Assert.That(code).Contains("Create_TestApp_IndexedItem");
  }

  /// <summary>
  /// Tests Dictionary with nested generic value type: Dictionary&lt;string, List&lt;T&gt;&gt;.
  /// The _findTopLevelComma helper must correctly parse nested angle brackets.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_findTopLevelComma</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DictionaryWithNestedGenericValue_DiscoversInnerTypeAsync() {
    // Arrange - Dictionary<string, List<CustomItem>> - need to discover CustomItem through the List
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record GroupedItem {
  public required string Label { get; init; }
}

public record GroupedDataEvent : IEvent {
  public required Dictionary<string, List<GroupedItem>> GroupedItems { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // GroupedItem should be discovered through List<GroupedItem> which is the Dictionary value
    await Assert.That(code).Contains("GroupedItem");
    await Assert.That(code).Contains("Create_TestApp_GroupedItem");
  }

  /// <summary>
  /// Tests Dictionary with primitive value type - should NOT trigger nested type discovery.
  /// Dictionary&lt;string, int&gt; should work without generating custom type info for int.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_isPrimitiveOrFrameworkType</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DictionaryWithPrimitiveValue_SkipsNestedDiscoveryAsync() {
    // Arrange - Dictionary<string, int> - no custom type to discover
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record CounterEvent : IEvent {
  public required Dictionary<string, int> Counters { get; init; }
  public required Dictionary<string, decimal> Amounts { get; init; }
  public required Dictionary<string, string> Labels { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Should succeed without discovering any nested types from Dictionary values
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Only the event itself should have factory, no primitive type factories
    await Assert.That(code).Contains("CounterEvent");
  }

  /// <summary>
  /// Tests nullable Dictionary property: Dictionary&lt;string, T&gt;?
  /// Nullable suffix should be stripped before extracting value type.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NullableDictionaryProperty_DiscoversValueTypeAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record OptionalConfig {
  public required string Setting { get; init; }
}

public record OptionalDataEvent : IEvent {
  public Dictionary<string, OptionalConfig>? OptionalConfigs { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Value type should still be discovered even with nullable Dictionary
    await Assert.That(code).Contains("OptionalConfig");
    await Assert.That(code).Contains("Create_TestApp_OptionalConfig");
  }

  /// <summary>
  /// Tests Dictionary value that is also a Dictionary: Dictionary&lt;string, Dictionary&lt;int, T&gt;&gt;.
  /// Nested Dictionary handling with proper comma parsing.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_findTopLevelComma</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NestedDictionaryValue_DiscoversDeepestTypeAsync() {
    // Arrange - Dictionary<string, Dictionary<int, DeepItem>>
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record DeepItem {
  public required string DeepValue { get; init; }
}

public record DeepNestedEvent : IEvent {
  public required Dictionary<string, Dictionary<int, DeepItem>> DeepMap { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // DeepItem should be discovered through the nested Dictionary chain
    await Assert.That(code).Contains("DeepItem");
    await Assert.That(code).Contains("Create_TestApp_DeepItem");
  }

  /// <summary>
  /// Tests multiple Dictionary properties with different value types.
  /// All unique value types should be discovered.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverNestedTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleDictionaryProperties_DiscoversAllValueTypesAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record UserProfile {
  public required string Username { get; init; }
}

public record Permission {
  public required string Name { get; init; }
  public required bool Granted { get; init; }
}

public record Setting {
  public required string Key { get; init; }
  public required string Value { get; init; }
}

public record SystemStateEvent : IEvent {
  public required Dictionary<string, UserProfile> Users { get; init; }
  public required Dictionary<string, Permission> Permissions { get; init; }
  public required Dictionary<string, Setting> Settings { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - All three value types should be discovered
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code).Contains("UserProfile");
    await Assert.That(code).Contains("Permission");
    await Assert.That(code).Contains("Setting");
    await Assert.That(code).Contains("Create_TestApp_UserProfile");
    await Assert.That(code).Contains("Create_TestApp_Permission");
    await Assert.That(code).Contains("Create_TestApp_Setting");
  }

  /// <summary>
  /// Tests the exact scenario from the bug report: JobTemplateSeedOrchestrationInitiatedEvent
  /// with Dictionary&lt;string, SeedSectionContext&gt;.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_BugReport_DictionarySeedSectionContext_DiscoversValueTypeAsync() {
    // Arrange - Exact reproduction of the bug report scenario
    const string source = """
using Whizbang.Core;
using System;
using System.Collections.Generic;

namespace TestApp.Orchestration;

public record SeedSectionContext {
  public required string SectionName { get; init; }
  public required Guid SectionId { get; init; }
}

public record JobTemplateSeedOrchestrationInitiatedEvent : IEvent {
  public required Dictionary<string, SeedSectionContext> SectionContexts { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - This was the exact failure case - SeedSectionContext must be discovered
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Without the fix, SeedSectionContext would NOT be present, causing runtime NotSupportedException
    await Assert.That(code).Contains("SeedSectionContext");
    await Assert.That(code).Contains("Create_TestApp_Orchestration_SeedSectionContext");

    // Also verify the event itself is present
    await Assert.That(code).Contains("JobTemplateSeedOrchestrationInitiatedEvent");
  }

  /// <summary>
  /// Tests triple nesting: Dictionary&lt;string, List&lt;Dictionary&lt;int, T&gt;&gt;&gt;.
  /// The recursive extraction must handle multiple levels of collection nesting.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementTypeSingleLevel</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_TripleNestedCollections_DiscoversDeepestTypeAsync() {
    // Arrange - Dictionary<string, List<Dictionary<int, TripleNestedItem>>>
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record TripleNestedItem {
  public required string TripleValue { get; init; }
}

public record TripleNestedEvent : IEvent {
  public required Dictionary<string, List<Dictionary<int, TripleNestedItem>>> TripleNested { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - TripleNestedItem should be discovered through all three levels
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code).Contains("TripleNestedItem");
    await Assert.That(code).Contains("Create_TestApp_TripleNestedItem");
  }

  /// <summary>
  /// Tests array inside Dictionary: Dictionary&lt;string, T[]&gt;.
  /// Array element types should be discovered from Dictionary values.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementTypeSingleLevel</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DictionaryWithArrayValue_DiscoversArrayElementTypeAsync() {
    // Arrange - Dictionary<string, ArrayItem[]>
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ArrayItem {
  public required string ArrayValue { get; init; }
}

public record ArrayDictEvent : IEvent {
  public required Dictionary<string, ArrayItem[]> ArrayDict { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - ArrayItem should be discovered through the array
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code).Contains("ArrayItem");
    await Assert.That(code).Contains("Create_TestApp_ArrayItem");
  }

  /// <summary>
  /// Tests multiple levels of List nesting: List&lt;List&lt;List&lt;T&gt;&gt;&gt;.
  /// Recursive extraction should handle any depth of List nesting.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementTypeSingleLevel</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_TripleNestedList_DiscoversDeepestTypeAsync() {
    // Arrange - List<List<List<DeepListItem>>>
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record DeepListItem {
  public required string DeepListValue { get; init; }
}

public record DeepListEvent : IEvent {
  public required List<List<List<DeepListItem>>> DeepList { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - DeepListItem should be discovered through all three List levels
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code).Contains("DeepListItem");
    await Assert.That(code).Contains("Create_TestApp_DeepListItem");
  }

  /// <summary>
  /// Tests IEnumerable nested in Dictionary: Dictionary&lt;string, IEnumerable&lt;T&gt;&gt;.
  /// All IEnumerable variants should be handled in recursive extraction.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementType</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_extractElementTypeSingleLevel</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DictionaryWithIEnumerableValue_DiscoversElementTypeAsync() {
    // Arrange - Dictionary<string, IEnumerable<EnumerableItem>>
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record EnumerableItem {
  public required string EnumerableValue { get; init; }
}

public record EnumerableDictEvent : IEvent {
  public required Dictionary<string, IEnumerable<EnumerableItem>> EnumerableDict { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - EnumerableItem should be discovered
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code).Contains("EnumerableItem");
    await Assert.That(code).Contains("Create_TestApp_EnumerableItem");
  }

  /// <summary>
  /// Tests _isCollectionType correctly identifies Dictionary types.
  /// Dictionary types should be treated as collections for nested type discovery.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_isCollectionType</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DictionaryAsDirectProperty_TreatedAsCollectionAsync() {
    // Arrange - Ensure Dictionary is correctly identified as a collection
    // and its value type is discovered, not the Dictionary itself
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record CollectionTestItem {
  public required string ItemName { get; init; }
}

public record CollectionTypeTestEvent : IEvent {
  // Dictionary should be treated as collection, value type discovered
  public required Dictionary<string, CollectionTestItem> DictItems { get; init; }

  // List should also be treated as collection (existing behavior)
  public required List<CollectionTestItem> ListItems { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // CollectionTestItem should be discovered only once (deduplication)
    await Assert.That(code).Contains("CollectionTestItem");
    await Assert.That(code).Contains("Create_TestApp_CollectionTestItem");
  }

  /// <summary>
  /// Tests that Dictionary types generate JsonTypeInfo factories.
  /// The generator must create CreateDictionary_* methods for AOT serialization.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverDictionaryTypes</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_generateDictionaryFactories</tests>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_generateDictionaryLazyFields</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithDictionaryProperty_GeneratesDictionaryFactoryAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record SectionConfig {
  public required string Name { get; init; }
}

public record ConfigEvent : IEvent {
  public required Dictionary<string, SectionConfig> Sections { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate Dictionary factory
    await Assert.That(code).Contains("CreateDictionary_");
    await Assert.That(code).Contains("Dictionary<string, global::TestApp.SectionConfig>");

    // Should also discover and generate factory for value type
    await Assert.That(code).Contains("SectionConfig");
    await Assert.That(code).Contains("Create_TestApp_SectionConfig");
  }

  /// <summary>
  /// Tests that multiple Dictionary properties generate all needed factories.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverDictionaryTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleDictionaryProperties_GeneratesAllFactoriesAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record TypeA { public required string A { get; init; } }
public record TypeB { public required string B { get; init; } }

public record MultiDictEvent : IEvent {
  public required Dictionary<string, TypeA> DictA { get; init; }
  public required Dictionary<int, TypeB> DictB { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate factories for both Dictionary types
    await Assert.That(code).Contains("Dictionary<string, global::TestApp.TypeA>");
    await Assert.That(code).Contains("Dictionary<global::System.Int32, global::TestApp.TypeB>");

    // Both value types should be discovered
    await Assert.That(code).Contains("Create_TestApp_TypeA");
    await Assert.That(code).Contains("Create_TestApp_TypeB");
  }

  /// <summary>
  /// Tests Dictionary with nested generic value generates correct factory.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_generateDictionaryFactories</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DictionaryWithNestedGenericValue_GeneratesFactoryAsync() {
    // Arrange - Dictionary<string, List<NestedItem>>
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record NestedItem {
  public required string Value { get; init; }
}

public record NestedDictEvent : IEvent {
  public required Dictionary<string, List<NestedItem>> NestedDict { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate Dictionary factory with List value type
    await Assert.That(code).Contains("CreateDictionary_");
    await Assert.That(code).Contains("List<global::TestApp.NestedItem>");

    // Should discover NestedItem through the nested List
    await Assert.That(code).Contains("NestedItem");
    await Assert.That(code).Contains("Create_TestApp_NestedItem");
  }

  #region IReadOnlyList<T> Type Generation Tests

  /// <summary>
  /// Tests that IReadOnlyList&lt;T&gt; property generates IReadOnlyList factory.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverIReadOnlyListTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MessageWithIReadOnlyListProperty_GeneratesIReadOnlyListFactoryAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record CatalogItem {
  public required string Name { get; init; }
}

public record CatalogEvent : IEvent {
  public required IReadOnlyList<CatalogItem> Items { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate IReadOnlyList factory
    await Assert.That(code).Contains("CreateIReadOnlyList_");
    await Assert.That(code).Contains("IReadOnlyList<global::TestApp.CatalogItem>");

    // Should also discover and generate factory for element type
    await Assert.That(code).Contains("CatalogItem");
    await Assert.That(code).Contains("Create_TestApp_CatalogItem");
  }

  /// <summary>
  /// Tests that multiple IReadOnlyList properties generate all needed factories.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverIReadOnlyListTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleIReadOnlyListProperties_GeneratesAllFactoriesAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record ItemA { public required string A { get; init; } }
public record ItemB { public required string B { get; init; } }

public record MultiListEvent : IEvent {
  public required IReadOnlyList<ItemA> ListA { get; init; }
  public required IReadOnlyList<ItemB> ListB { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate factories for both IReadOnlyList types
    await Assert.That(code).Contains("IReadOnlyList<global::TestApp.ItemA>");
    await Assert.That(code).Contains("IReadOnlyList<global::TestApp.ItemB>");

    // Both element types should be discovered
    await Assert.That(code).Contains("Create_TestApp_ItemA");
    await Assert.That(code).Contains("Create_TestApp_ItemB");
  }

  /// <summary>
  /// Tests that IReadOnlyList with nested generic element type generates correct factory.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_generateIReadOnlyListFactories</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_IReadOnlyListWithNestedGenericElement_GeneratesFactoryAsync() {
    // Arrange - IReadOnlyList<List<NestedItem>>
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record NestedItem {
  public required string Value { get; init; }
}

public record NestedListEvent : IEvent {
  public required IReadOnlyList<List<NestedItem>> NestedLists { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate IReadOnlyList factory
    await Assert.That(code).Contains("CreateIReadOnlyList_");

    // Should discover NestedItem through nested extraction
    await Assert.That(code).Contains("Create_TestApp_NestedItem");
  }

  /// <summary>
  /// Bug report reproduction: IReadOnlyList&lt;JobTemplateFieldCatalogItem&gt; should generate factory.
  /// </summary>
  /// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs:_discoverIReadOnlyListTypes</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_BugReport_IReadOnlyListCatalogItem_GeneratesFactoryAsync() {
    // Arrange - Reproduces the actual bug scenario
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace JDX.Contracts.Job;

public record JobTemplateFieldCatalogItem {
  public required string Name { get; init; }
  public required string Type { get; init; }
}

public record JobTemplateFieldCatalogInitializedEvent : IEvent {
  public required IReadOnlyList<JobTemplateFieldCatalogItem> Items { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate IReadOnlyList factory for JobTemplateFieldCatalogItem
    await Assert.That(code).Contains("CreateIReadOnlyList_");
    await Assert.That(code).Contains("IReadOnlyList<global::JDX.Contracts.Job.JobTemplateFieldCatalogItem>");

    // Should generate type info check for IReadOnlyList
    await Assert.That(code).Contains("typeof(global::System.Collections.Generic.IReadOnlyList<global::JDX.Contracts.Job.JobTemplateFieldCatalogItem>)");
  }

  /// <summary>
  /// Tests that IReadOnlyList&lt;T&gt; factory does NOT use CreateListInfo (which has IList constraint).
  /// IReadOnlyList&lt;T&gt; doesn't implement IList&lt;T&gt;, so CreateListInfo won't compile.
  /// Must use CreateIEnumerableInfo or similar API that works with read-only collections.
  /// </summary>
  /// <tests>src/Whizbang.Generators/Templates/Snippets/JsonContextSnippets.cs:IREADONLYLIST_TYPE_FACTORY</tests>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_IReadOnlyListFactory_DoesNotUseCreateListInfoAsync() {
    // Arrange
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record CatalogItem {
  public required string Name { get; init; }
}

public record CatalogEvent : IEvent {
  public required IReadOnlyList<CatalogItem> Items { get; init; }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - Generator should produce no errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Get generated code
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // The IReadOnlyList factory should NOT use CreateListInfo (IList<T> constraint violation)
    // It should use CreateIEnumerableInfo or similar API for read-only collections
    await Assert.That(code).DoesNotContain("CreateListInfo<global::System.Collections.Generic.IReadOnlyList<");

    // Should use the correct API for IReadOnlyList
    await Assert.That(code).Contains("CreateIReadOnlyList_");
  }

  #endregion

  #region Polymorphic Interface Support Tests

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithEvent_GeneratesDerivedTypeRegistrationAsync() {
    // Arrange
    const string source = @"
using Whizbang.Core;

namespace MyApp.Events;

public record OrderPlaced(Guid OrderId) : IEvent;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Should generate MessageJsonContextInitializer with derived type registration
    var initializerCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContextInitializer.g.cs");
    await Assert.That(initializerCode).IsNotNull();
    await Assert.That(initializerCode).Contains("RegisterDerivedType<global::Whizbang.Core.IEvent, global::MyApp.Events.OrderPlaced>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithCommand_GeneratesDerivedTypeRegistrationAsync() {
    // Arrange
    const string source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(Guid OrderId, string CustomerName) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    // Should generate MessageJsonContextInitializer with derived type registration
    var initializerCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContextInitializer.g.cs");
    await Assert.That(initializerCode).IsNotNull();
    await Assert.That(initializerCode).Contains("RegisterDerivedType<global::Whizbang.Core.ICommand, global::MyApp.Commands.CreateOrder>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleEvents_GeneratesAllDerivedTypeRegistrationsAsync() {
    // Arrange - multiple event types
    const string source = @"
using Whizbang.Core;

namespace MyApp.Events;

public record OrderPlaced(Guid OrderId) : IEvent;
public record OrderShipped(Guid OrderId, string TrackingNumber) : IEvent;
public record OrderDelivered(Guid OrderId) : IEvent;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var initializerCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContextInitializer.g.cs");
    await Assert.That(initializerCode).IsNotNull();

    // Should register all event types
    await Assert.That(initializerCode).Contains("RegisterDerivedType<global::Whizbang.Core.IEvent, global::MyApp.Events.OrderPlaced>");
    await Assert.That(initializerCode).Contains("RegisterDerivedType<global::Whizbang.Core.IEvent, global::MyApp.Events.OrderShipped>");
    await Assert.That(initializerCode).Contains("RegisterDerivedType<global::Whizbang.Core.IEvent, global::MyApp.Events.OrderDelivered>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMixedMessageTypes_GeneratesCorrectBaseTypeRegistrationsAsync() {
    // Arrange - mix of commands and events
    const string source = @"
using Whizbang.Core;

namespace MyApp;

public record CreateOrder(Guid OrderId) : ICommand;
public record OrderCreated(Guid OrderId) : IEvent;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var initializerCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContextInitializer.g.cs");
    await Assert.That(initializerCode).IsNotNull();

    // Should register command with ICommand base type
    await Assert.That(initializerCode).Contains("RegisterDerivedType<global::Whizbang.Core.ICommand, global::MyApp.CreateOrder>");

    // Should register event with IEvent base type
    await Assert.That(initializerCode).Contains("RegisterDerivedType<global::Whizbang.Core.IEvent, global::MyApp.OrderCreated>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DerivedTypeRegistration_UsesFullyQualifiedNameAsDiscriminatorAsync() {
    // Arrange
    const string source = @"
using Whizbang.Core;

namespace MyApp.Events;

public record OrderPlaced(Guid OrderId) : IEvent;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var initializerCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContextInitializer.g.cs");
    await Assert.That(initializerCode).IsNotNull();

    // Should use fully qualified type name as discriminator to avoid collisions
    await Assert.That(initializerCode).Contains("RegisterDerivedType<global::Whizbang.Core.IEvent, global::MyApp.Events.OrderPlaced>(\"MyApp.Events.OrderPlaced\")");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_Initializer_HasModuleInitializerAttributeAsync() {
    // Arrange
    const string source = @"
using Whizbang.Core;

namespace MyApp.Events;

public record OrderPlaced(Guid OrderId) : IEvent;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var initializerCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContextInitializer.g.cs");
    await Assert.That(initializerCode).IsNotNull();

    // Should have ModuleInitializer attribute
    await Assert.That(initializerCode).Contains("[ModuleInitializer]");
    await Assert.That(initializerCode).Contains("public static void Initialize()");
  }

  #endregion

  #region List<List<primitive>> Tests

  /// <summary>
  /// Verifies that GetOrCreateTypeInfo handles List&lt;List&lt;string&gt;&gt; for AOT serialization.
  /// STJ in AOT mode requires explicit JsonTypeInfo for nested collections - it does NOT handle them natively.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesListOfListOfStringHandlingAsync() {
    // Arrange
    const string source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate List<List<string>> handling for deeply nested collections
    await Assert.That(code).Contains("if (type == typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<string>>))");
    await Assert.That(code).Contains("CreateListInfo<global::System.Collections.Generic.List<global::System.Collections.Generic.List<string>>, global::System.Collections.Generic.List<string>>");
  }

  /// <summary>
  /// Verifies that GetOrCreateTypeInfo handles List&lt;List&lt;int&gt;&gt; for AOT serialization.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesListOfListOfIntHandlingAsync() {
    // Arrange
    const string source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate List<List<int>> handling
    await Assert.That(code).Contains("if (type == typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<int>>))");
    await Assert.That(code).Contains("CreateListInfo<global::System.Collections.Generic.List<global::System.Collections.Generic.List<int>>, global::System.Collections.Generic.List<int>>");
  }

  /// <summary>
  /// Verifies that all primitive List&lt;List&lt;T&gt;&gt; types are generated.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesAllListOfListOfPrimitiveTypesAsync() {
    // Arrange
    const string source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert - No compilation errors
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate List<List<T>> handling for all primitive types
    await Assert.That(code).Contains("List<global::System.Collections.Generic.List<string>>"); // string
    await Assert.That(code).Contains("List<global::System.Collections.Generic.List<int>>"); // int
    await Assert.That(code).Contains("List<global::System.Collections.Generic.List<long>>"); // long
    await Assert.That(code).Contains("List<global::System.Collections.Generic.List<bool>>"); // bool
    await Assert.That(code).Contains("List<global::System.Collections.Generic.List<Guid>>"); // Guid
    await Assert.That(code).Contains("List<global::System.Collections.Generic.List<decimal>>"); // decimal
    await Assert.That(code).Contains("List<global::System.Collections.Generic.List<double>>"); // double
    await Assert.That(code).Contains("List<global::System.Collections.Generic.List<float>>"); // float
  }

  #endregion

  #region IPerspectiveWithActionsFor event type discovery

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_IPerspectiveWithActionsFor_ModelIncludedInJsonContextAsync() {
    // Arrange — Perspective using only IPerspectiveWithActionsFor (Purge pattern)
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestApp {
  public record PurgeEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record PurgeModel {
    [StreamId]
    public Guid Id { get; init; }
    public string Name { get; init; } = """";
  }

  public class PurgePerspective : IPerspectiveWithActionsFor<PurgeModel, PurgeEvent> {
    public ApplyResult<PurgeModel> Apply(PurgeModel current, PurgeEvent @event)
      => ApplyResult<PurgeModel>.Purge();
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    // Assert — PurgeModel must be discovered and included in JSON context
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("PurgeModel")
      .Because("IPerspectiveWithActionsFor models must be included in MessageJsonContext for serialization");
  }

  #endregion
}
