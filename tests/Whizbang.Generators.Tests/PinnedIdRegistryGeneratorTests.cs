using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for PinnedIdRegistryGenerator.
/// Validates discovery of [PinnedId] attributes on IMessage and IPerspectiveFor&lt;&gt; types
/// and emission of an AOT-safe IPinnedIdRegistry implementation.
/// </summary>
public class PinnedIdRegistryGeneratorTests {

  [Test]
  public async Task Generator_WithPinnedEvents_GeneratesRegistryAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [PinnedId("11111111-2222-3333-4444-555555555555")]
      public record OrderPlacedEvent : IEvent;

      [PinnedId("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
      public record OrderShippedEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode!).Contains(": IPinnedIdRegistry");
    await Assert.That(registryCode!).Contains("typeof(global::MyApp.Events.OrderPlacedEvent)");
    await Assert.That(registryCode!).Contains("\"11111111-2222-3333-4444-555555555555\"");
    await Assert.That(registryCode!).Contains("typeof(global::MyApp.Events.OrderShippedEvent)");
    await Assert.That(registryCode!).Contains("\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"");
  }

  [Test]
  public async Task Generator_WithPinnedCommands_GeneratesRegistryAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Commands;

      [PinnedId("cccccccc-1111-2222-3333-444444444444")]
      public record PlaceOrderCommand : ICommand;

""";

    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode!).Contains("typeof(global::MyApp.Commands.PlaceOrderCommand)");
    await Assert.That(registryCode!).Contains("\"cccccccc-1111-2222-3333-444444444444\"");
  }

  [Test]
  public async Task Generator_WithPinnedPerspectives_GeneratesRegistryAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      using Whizbang.Core.Perspectives;

      namespace MyApp.Views;

      public record OrderView;
      public record OrderPlacedEvent : IEvent;

      [PinnedId("deadbeef-1111-2222-3333-444444444444")]
      public class OrderPerspective : IPerspectiveFor<OrderView, OrderPlacedEvent> {
        public OrderView Apply(OrderView? current, OrderPlacedEvent @event) => current ?? new();
      }

""";

    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode!).Contains("typeof(global::MyApp.Views.OrderPerspective)");
    await Assert.That(registryCode!).Contains("\"deadbeef-1111-2222-3333-444444444444\"");
  }

  [Test]
  public async Task Generator_WithPinnedPerspectiveWithActionsFor_GeneratesRegistryAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      using Whizbang.Core.Perspectives;

      namespace MyApp.Views;

      public record OrderView;
      public record OrderShippedEvent : IEvent;

      [PinnedId("c0ffee00-1111-2222-3333-444444444444")]
      public class ActionsOrderPerspective : IPerspectiveWithActionsFor<OrderView, OrderShippedEvent> {
        public ApplyResult<OrderView> Apply(OrderView? current, OrderShippedEvent @event) =>
          ApplyResult<OrderView>.Update(current ?? new());
      }

""";

    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode!).Contains("typeof(global::MyApp.Views.ActionsOrderPerspective)");
    await Assert.That(registryCode!).Contains("\"c0ffee00-1111-2222-3333-444444444444\"");
  }

  [Test]
  public async Task Generator_WithoutPinnedId_SkipsTypeAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      // No [PinnedId] - should not appear in registry
      public record UnpinnedEvent : IEvent;

      [PinnedId("99999999-8888-7777-6666-555555555555")]
      public record PinnedEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode!).Contains("typeof(global::MyApp.Events.PinnedEvent)");
    await Assert.That(registryCode!).DoesNotContain("UnpinnedEvent");
  }

  [Test]
  public async Task Generator_WithAbstractType_SkipsItAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [PinnedId("12345678-1234-1234-1234-123456789012")]
      public abstract record BaseEvent : IEvent;

      [PinnedId("87654321-4321-4321-4321-210987654321")]
      public record ConcreteEvent : BaseEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode!).DoesNotContain("BaseEvent");
    await Assert.That(registryCode!).Contains("typeof(global::MyApp.Events.ConcreteEvent)");
  }

  [Test]
  public async Task Generator_WithNoPinnedTypes_GeneratesNothingAsync() {
    const string source = """

      namespace MyApp;

      public class SomeClass {
        public void DoSomething() { }
      }

""";

    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(source);

    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");
    await Assert.That(registryCode).IsNull();
  }

  [Test]
  public async Task Generator_GeneratesAddPinnedIdRegistryExtensionAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [PinnedId("11111111-1111-1111-1111-111111111111")]
      public record SomeEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(source);

    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode!).Contains("AddPinnedIdRegistry");
    await Assert.That(registryCode!).Contains("AddSingleton<IPinnedIdRegistry");
  }

  [Test]
  public async Task Generator_EmitsModuleInitializerThatRegistersViaAddWhizbangAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [PinnedId("22222222-2222-2222-2222-222222222222")]
      public record SomeEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(source);
    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");

    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode!).Contains("[System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(registryCode!).Contains("ServiceRegistrationCallbacks.PinnedIdRegistry");
    await Assert.That(registryCode!).Contains("AddSingleton<IPinnedIdRegistry, GeneratedPinnedIdRegistry>");
  }

  [Test]
  public async Task Generator_UnpinnedType_ReturnsNullFromGeneratedGetPinnedIdAsync() {
    // The generated code should return null for types not in the registry;
    // we check this by confirming the method ends with "return null;".
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [PinnedId("11111111-1111-1111-1111-111111111111")]
      public record SomeEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(source);
    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");

    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode!).Contains("public string? GetPinnedId(Type type)");
    await Assert.That(registryCode!).Contains("return null;");
  }
}
