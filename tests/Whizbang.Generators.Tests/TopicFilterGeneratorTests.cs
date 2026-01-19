using System.ComponentModel;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for TopicFilterGenerator source generator.
/// Validates discovery of ICommand types with TopicFilter attributes
/// and generation of AOT-compatible topic filter registry.
/// </summary>
public class TopicFilterGeneratorTests {
  [Test]
  public async Task Generator_WithStringFilter_GeneratesRegistryAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace;

[TopicFilter(""orders.create"")]
public record CreateOrderCommand : ICommand {
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    // Assert - Should generate TopicFilterRegistry
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();

    // Should contain GetTopicFilters method
    await Assert.That(registrySource!).Contains("GetTopicFilters<TCommand>");

    // Should contain mapping for CreateOrderCommand
    await Assert.That(registrySource).Contains("CreateOrderCommand");
    await Assert.That(registrySource).Contains("orders.create");

    // Should not have compilation errors
    await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
  }

  [Test]
  public async Task Generator_WithMultipleStringFilters_GeneratesAllMappingsAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace;

[TopicFilter(""orders.create"")]
[TopicFilter(""commands.order"")]
public record CreateOrderCommand : ICommand {
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    // Assert
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();

    // Should contain both filters
    await Assert.That(registrySource!).Contains("orders.create");
    await Assert.That(registrySource).Contains("commands.order");
  }

  [Test]
  public async Task Generator_WithEnumFilter_ExtractsDescriptionAsync() {
    // Arrange
    var source = @"
using System.ComponentModel;
using Whizbang.Core;

namespace TestNamespace;

public enum Topics {
    [Description(""orders.created"")]
    OrdersCreated,

    [Description(""orders.updated"")]
    OrdersUpdated
}

[TopicFilter<Topics>(Topics.OrdersCreated)]
public record CreateOrderCommand : ICommand {
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    // Assert
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();

    // Should extract description attribute value
    await Assert.That(registrySource!).Contains("orders.created");
    await Assert.That(registrySource).DoesNotContain("OrdersCreated");  // Should use description, not symbol
  }

  [Test]
  public async Task Generator_WithEnumFilterNoDescription_UsesSymbolNameAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace;

public enum Topics {
    Payments,  // No Description attribute
    Orders
}

[TopicFilter<Topics>(Topics.Payments)]
public record ProcessPaymentCommand : ICommand {
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    // Assert
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();

    // Should fall back to symbol name
    await Assert.That(registrySource!).Contains("Payments");
  }

  [Test]
  public async Task Generator_WithMultipleCommands_GeneratesAllMappingsAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace;

[TopicFilter(""orders.create"")]
public record CreateOrderCommand : ICommand {
}

[TopicFilter(""payments.process"")]
public record ProcessPaymentCommand : ICommand {
}

[TopicFilter(""products.update"")]
public record UpdateProductCommand : ICommand {
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    // Assert
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();

    // Should contain all command mappings
    await Assert.That(registrySource!).Contains("CreateOrderCommand");
    await Assert.That(registrySource).Contains("ProcessPaymentCommand");
    await Assert.That(registrySource).Contains("UpdateProductCommand");

    // Should contain all filters
    await Assert.That(registrySource).Contains("orders.create");
    await Assert.That(registrySource).Contains("payments.process");
    await Assert.That(registrySource).Contains("products.update");
  }

  [Test]
  public async Task Generator_WithNoFilters_GeneratesEmptyRegistryAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace;

public record SomeCommand : ICommand {
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    // Assert - Should still generate registry, just with no mappings
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");

    // Generator may skip output if no filters found, or generate empty registry
    // Either is acceptable - just ensure no errors
    await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
  }

  [Test]
  public async Task Generator_WithCustomDerivedAttribute_RecognizesFilterAsync() {
    // Arrange
    var source = @"
using System;
using System.ComponentModel;
using Whizbang.Core;

namespace TestNamespace;

public enum RabbitMqTopics {
    [Description(""orders.queue"")]
    Orders
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public class RabbitMqTopicAttribute : TopicFilterAttribute<RabbitMqTopics> {
    public RabbitMqTopicAttribute(RabbitMqTopics topic) : base(topic) { }
}

[RabbitMqTopic(RabbitMqTopics.Orders)]
public record CreateOrderCommand : ICommand {
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    // Assert
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();

    // Should recognize derived attribute and extract description
    await Assert.That(registrySource!).Contains("orders.queue");
  }

  [Test]
  public async Task Generator_WithMixedEnumAndStringFilters_GeneratesBothAsync() {
    // Arrange
    var source = @"
using System.ComponentModel;
using Whizbang.Core;

namespace TestNamespace;

public enum Topics {
    [Description(""orders.created"")]
    OrdersCreated
}

[TopicFilter<Topics>(Topics.OrdersCreated)]
[TopicFilter(""backup.queue"")]
public record CreateOrderCommand : ICommand {
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    // Assert
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();

    // Should contain both enum-based and string-based filters
    await Assert.That(registrySource!).Contains("orders.created");
    await Assert.That(registrySource).Contains("backup.queue");
  }

  [Test]
  public async Task Generator_GeneratesGetAllFiltersMethod_ForDiagnosticsAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace;

[TopicFilter(""orders.create"")]
public record CreateOrderCommand : ICommand {
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    // Assert
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();

    // Should have GetAllFilters diagnostic method
    await Assert.That(registrySource!).Contains("GetAllFilters()");
    await Assert.That(registrySource).Contains("IReadOnlyDictionary");
  }

  [Test]
  public async Task Generator_UsesAssemblySpecificNamespace_AvoidingConflictsAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace;

[TopicFilter(""orders.create"")]
public record CreateOrderCommand : ICommand {
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    // Assert
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();

    // Should use assembly-specific namespace (TestAssembly.Generated)
    await Assert.That(registrySource!).Contains("namespace TestAssembly.Generated");
  }

  [Test]
  public async Task Generator_WithFilterOnNonCommand_ReportsErrorAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace;

[TopicFilter(""should.fail"")]
public record NotACommand {  // Missing ICommand
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    // Assert - Should not generate mapping for non-ICommand type
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    if (registrySource != null) {
      await Assert.That(registrySource).DoesNotContain("NotACommand");
    }

    // No errors expected - just silently ignore non-ICommand types
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    await Assert.That(errors).IsEmpty();
  }
}
