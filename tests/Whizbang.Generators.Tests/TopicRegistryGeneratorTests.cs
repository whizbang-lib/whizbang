using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for TopicRegistryGenerator.
/// Validates discovery of [Topic] attributes and convention-based routing.
/// </summary>
public class TopicRegistryGeneratorTests {

  [Test]
  public async Task Generator_WithTopicAttribute_GeneratesRegistryAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [Topic(""products"")]
      public record ProductCreatedEvent : IEvent;

      [Topic(""inventory"")]
      public record InventoryRestockedEvent : IEvent;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicRegistryGenerator>(source);

    // Assert - No errors
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    // Assert - Registry generated
    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "TopicRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.ProductCreatedEvent)");
    await Assert.That(registryCode).Contains("return \"products\";");
    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.InventoryRestockedEvent)");
    await Assert.That(registryCode).Contains("return \"inventory\";");
  }

  [Test]
  public async Task Generator_WithConventionBasedRouting_GeneratesRegistryAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      namespace MyApp.Events;

      // No [Topic] attribute, should use convention (Product* → products)
      public record ProductCreatedEvent : IEvent;

      // No [Topic] attribute, should use convention (Inventory* → inventory)
      public record InventoryRestockedEvent : IEvent;

      // No [Topic] attribute, should use convention (Order* → orders)
      public record OrderCreatedEvent : IEvent;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicRegistryGenerator>(source);

    // Assert - No errors
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    // Assert - Registry generated with convention-based topics
    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "TopicRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();

    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.ProductCreatedEvent)");
    await Assert.That(registryCode).Contains("return \"products\";");
    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.InventoryRestockedEvent)");
    await Assert.That(registryCode).Contains("return \"inventory\";");
    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.OrderCreatedEvent)");
    await Assert.That(registryCode).Contains("return \"orders\";");
  }

  [Test]
  public async Task Generator_WithCommandTypes_GeneratesRegistryAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Commands;

      [Topic(""products"")]
      public record CreateProductCommand : ICommand;

      // Convention-based (Product* → products)
      public record UpdateProductCommand : ICommand;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicRegistryGenerator>(source);

    // Assert - No errors
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    // Assert - Registry includes commands
    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "TopicRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();

    await Assert.That(registryCode).Contains("typeof(global::MyApp.Commands.CreateProductCommand)");
    await Assert.That(registryCode).Contains("return \"products\";");
    await Assert.That(registryCode).Contains("typeof(global::MyApp.Commands.UpdateProductCommand)");
  }

  [Test]
  public async Task Generator_WithPrivateTypes_SkipsThemAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [Topic(""products"")]
      public record ProductCreatedEvent : IEvent;

      [Topic(""internal-event"")]
      internal record InternalEvent : IEvent;

      [Topic(""private-event"")]
      private record PrivateEvent : IEvent;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicRegistryGenerator>(source);

    // Assert - No compilation errors
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    // Assert - Registry only includes public types
    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "TopicRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();

    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.ProductCreatedEvent)");
    await Assert.That(registryCode).DoesNotContain("InternalEvent");
    await Assert.That(registryCode).DoesNotContain("PrivateEvent");
  }

  [Test]
  public async Task Generator_WithAbstractTypes_SkipsThemAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [Topic(""products"")]
      public abstract record BaseEvent : IEvent;

      public record ProductCreatedEvent : BaseEvent;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicRegistryGenerator>(source);

    // Assert - No errors
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    // Assert - Registry skips abstract base, includes concrete type
    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "TopicRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();

    await Assert.That(registryCode).DoesNotContain("BaseEvent");
    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.ProductCreatedEvent)");
    await Assert.That(registryCode).Contains("return \"products\";");  // Uses convention (Product* → products)
  }

  [Test]
  public async Task Generator_WithNoMessageTypes_GeneratesNothingAsync() {
    // Arrange
    var source = @"
      namespace MyApp;

      public class SomeClass {
        public void DoSomething() { }
      }
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicRegistryGenerator>(source);

    // Assert - No errors
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    // Assert - No registry generated (empty topics array)
    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "TopicRegistry.g.cs");
    await Assert.That(registryCode).IsNull();  // Generator returns early if no topics
  }

  [Test]
  public async Task Generator_GeneratesAddTopicRegistryExtensionAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [Topic(""products"")]
      public record ProductCreatedEvent : IEvent;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicRegistryGenerator>(source);

    // Assert - Extension method generated
    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "TopicRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();

    await Assert.That(registryCode).Contains("public static class TopicRegistryExtensions");
    await Assert.That(registryCode).Contains("public static IServiceCollection AddTopicRegistry");
    await Assert.That(registryCode).Contains("services.AddSingleton<ITopicRegistry, TopicRegistry>();");
  }

  [Test]
  public async Task Generator_WithFallbackConvention_RemovesEventSuffixAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      namespace MyApp.Events;

      // Type doesn't match Product/Inventory/Order convention
      // Should fall back to: Remove ""Event"" suffix, lowercase
      public record SomethingHappenedEvent : IEvent;

      public record AnotherThingCommand : ICommand;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicRegistryGenerator>(source);

    // Assert - No errors
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    // Assert - Fallback convention applied
    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "TopicRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();

    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.SomethingHappenedEvent)");
    await Assert.That(registryCode).Contains("return \"somethinghappened\";");  // "Event" removed, lowercased
    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.AnotherThingCommand)");
    await Assert.That(registryCode).Contains("return \"anotherthing\";");  // "Command" removed, lowercased
  }

  [Test]
  public async Task Generator_WithMixedAttributeAndConvention_UsesBothAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [Topic(""custom-topic"")]
      public record ExplicitTopicEvent : IEvent;

      public record ProductCreatedEvent : IEvent;  // Convention: products

      [Topic(""another-custom"")]
      public record InventoryRestockedEvent : IEvent;

      public record OrderCreatedEvent : IEvent;  // Convention: orders
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicRegistryGenerator>(source);

    // Assert - No errors
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    // Assert - Both attribute and convention-based topics present
    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "TopicRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();


    // Attribute-based
    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.ExplicitTopicEvent)");
    await Assert.That(registryCode).Contains("return \"custom-topic\";");
    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.InventoryRestockedEvent)");
    await Assert.That(registryCode).Contains("return \"another-custom\";");

    // Convention-based
    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.ProductCreatedEvent)");
    await Assert.That(registryCode).Contains("return \"products\";");
    await Assert.That(registryCode).Contains("typeof(global::MyApp.Events.OrderCreatedEvent)");
    await Assert.That(registryCode).Contains("return \"orders\";");
  }
}
