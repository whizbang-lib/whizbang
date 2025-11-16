using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for JsonTypeInfoGenerator source generator.
/// Verifies zero-reflection JSON serialization for AOT compatibility using manual JsonTypeInfo generation.
/// </summary>
[Category("SourceGenerators")]
[Category("JsonSerialization")]
public class JsonTypeInfoGeneratorTests {

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
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("namespace Whizbang.Core.Generated");
    await Assert.That(code).Contains("public partial class WhizbangJsonContext : JsonSerializerContext");
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
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
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
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
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
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should override GetTypeInfo with switch
    await Assert.That(code!).Contains("public override JsonTypeInfo? GetTypeInfo(Type type)");
    await Assert.That(code).Contains("if (type == typeof(");
    await Assert.That(code).Contains("return null");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesGenericMessageEnvelopeHelperAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate generic CreateMessageEnvelope<T> helper
    await Assert.That(code!).Contains("CreateMessageEnvelope<T>");
    await Assert.That(code).Contains("where T : class");
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
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
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
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should always generate factories for core Whizbang types
    await Assert.That(code!).Contains("Create_MessageId");
    await Assert.That(code).Contains("Create_CorrelationId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesLazyInitializationFieldsAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate nullable fields for lazy initialization
    await Assert.That(code!).Contains("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.MessageId>? _MessageId;");
    await Assert.That(code).Contains("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.CorrelationId>? _CorrelationId;");
    await Assert.That(code).Contains("private JsonTypeInfo<MessageEnvelope<");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesPropertiesWithNullCoalescingAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Commands;

public record CreateOrder(string OrderId) : ICommand;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate properties with ??= for lazy initialization
    await Assert.That(code!).Contains("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.MessageId> MessageId => _MessageId ??= Create_MessageId(Options);");
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
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert - Should report WHIZ011 diagnostic
    var infos = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToArray();
    var jsonDiagnostic = infos.FirstOrDefault(d => d.Id == "WHIZ011");

    await Assert.That(jsonDiagnostic).IsNotNull();
    await Assert.That(jsonDiagnostic!.GetMessage()).Contains("CreateOrder");
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
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Should generate WhizbangJsonContext with only core types
    await Assert.That(code!).Contains("public partial class WhizbangJsonContext");
    await Assert.That(code).Contains("MessageId");
    await Assert.That(code).Contains("CorrelationId");

    // Should NOT contain any user message types
    await Assert.That(code).DoesNotContain("MyApp");
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
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
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
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert - Should generate context but not include OrderDto
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
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
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert - Should still generate context with core types
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("WhizbangJsonContext");
    await Assert.That(code).Contains("MessageId"); // Core type should be present
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task JsonTypeInfoGenerator_TypeImplementingBothInterfaces_GeneratesAsCommandAsync() {
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
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert - Should generate as command (ternary picks command when both true)
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("HybridMessage");

    // Check diagnostic reports it as command (not event) when both interfaces present
    var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ011");
    await Assert.That(diagnostic).IsNotNull();
    await Assert.That(diagnostic!.GetMessage()).Contains("command");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task JsonTypeInfoGenerator_ClassImplementingICommand_GeneratesJsonTypeInfoAsync() {
    // Arrange - Tests line 46-50: Class (not record) in switch expression
    var source = """
using Whizbang.Core;

namespace MyApp;

public class LegacyCommand : ICommand {
  public string Data { get; set; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<JsonTypeInfoGenerator>(source);

    // Assert - Should generate JsonTypeInfo for class-based command
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("LegacyCommand");

    // Check diagnostic reports it as command
    var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ011");
    await Assert.That(diagnostic).IsNotNull();
    await Assert.That(diagnostic!.GetMessage()).Contains("command");
  }
}
