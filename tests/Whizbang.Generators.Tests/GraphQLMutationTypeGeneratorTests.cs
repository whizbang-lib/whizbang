using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Whizbang.Transports.HotChocolate.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <see cref="GraphQLMutationTypeGenerator"/>.
/// Verifies that GraphQL mutation types are correctly generated from [CommandEndpoint] attributes.
/// </summary>
/// <tests>Whizbang.Transports.HotChocolate.Generators/GraphQLMutationTypeGenerator.cs</tests>
[Category("Generators")]
[Category("HotChocolate")]
[Category("Mutations")]
public class GraphQLMutationTypeGeneratorTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithValidCommandEndpoint_GeneratesCodeAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class CreateOrderResult {
        public bool Success { get; init; }
      }

      [CommandEndpoint<CreateOrderCommand, CreateOrderResult>(
          GraphQLMutation = "createOrder")]
      public class CreateOrderCommand : ICommand {
        public required string CustomerId { get; init; }
      }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("CreateOrderCommandMutation");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithoutGraphQLMutation_DoesNotGenerateCodeAsync() {
    // Arrange - only has RestRoute, no GraphQLMutation
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class CreateOrderResult {
        public bool Success { get; init; }
      }

      [CommandEndpoint<CreateOrderCommand, CreateOrderResult>(
          RestRoute = "/api/orders")]
      public class CreateOrderCommand : ICommand {
        public required string CustomerId { get; init; }
      }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNull();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_CreatesPartialClassAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class OrderResult { }

      [CommandEndpoint<PlaceOrderCommand, OrderResult>(GraphQLMutation = "placeOrder")]
      public class PlaceOrderCommand : ICommand { }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("public partial class PlaceOrderCommandMutation");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_InheritsFromGraphQLMutationBaseAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class OrderResult { }

      [CommandEndpoint<ProcessOrderCommand, OrderResult>(GraphQLMutation = "processOrder")]
      public class ProcessOrderCommand : ICommand { }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("GraphQLMutationBase<");
    await Assert.That(code).Contains("ProcessOrderCommand");
    await Assert.That(code).Contains("OrderResult");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AddsExtendObjectTypeAttributeAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class OrderResult { }

      [CommandEndpoint<ConfirmOrderCommand, OrderResult>(GraphQLMutation = "confirmOrder")]
      public class ConfirmOrderCommand : ICommand { }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("[ExtendObjectType(HotChocolate.Language.OperationTypeNames.Mutation)]");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_CreatesMutationMethodWithCorrectNameAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class OrderResult { }

      [CommandEndpoint<CancelOrderCommand, OrderResult>(GraphQLMutation = "cancelOrder")]
      public class CancelOrderCommand : ICommand { }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("public async Task<");
    await Assert.That(code).Contains("CancelOrderAsync");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_InjectsDispatcherAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class OrderResult { }

      [CommandEndpoint<RefundOrderCommand, OrderResult>(GraphQLMutation = "refundOrder")]
      public class RefundOrderCommand : ICommand { }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("IDispatcher _dispatcher");
    await Assert.That(code).Contains("public RefundOrderCommandMutation(IDispatcher dispatcher)");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_DispatchesViaLocalInvokeAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class OrderResult { }

      [CommandEndpoint<ShipOrderCommand, OrderResult>(GraphQLMutation = "shipOrder")]
      public class ShipOrderCommand : ICommand { }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("_dispatcher.LocalInvokeAsync<");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_UsesCorrectNamespaceAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace MyCompany.Orders.Commands;

      public class OrderResult { }

      [CommandEndpoint<TestCommand, OrderResult>(GraphQLMutation = "testMutation")]
      public class TestCommand : ICommand { }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("namespace MyCompany.Orders.Commands.Generated");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithCustomRequestType_GeneratesExecuteWithRequestAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class CreateOrderRequest {
        public required string CustomerEmail { get; init; }
      }

      public class CreateOrderResult { }

      [CommandEndpoint<CreateOrderCommand, CreateOrderResult>(
          GraphQLMutation = "createOrder",
          RequestType = typeof(CreateOrderRequest))]
      public class CreateOrderCommand : ICommand {
        public required string CustomerId { get; init; }
      }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("CreateOrderRequest request");
    await Assert.That(code).Contains("ExecuteWithRequestAsync");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithoutCustomRequestType_UsesCommandAsParameterAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class SimpleResult { }

      [CommandEndpoint<SimpleCommand, SimpleResult>(GraphQLMutation = "simpleAction")]
      public class SimpleCommand : ICommand {
        public required string Value { get; init; }
      }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("SimpleCommand command");
    await Assert.That(code).Contains("ExecuteAsync(command");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_HandlesMultipleMutationsAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class Result1 { }
      public class Result2 { }

      [CommandEndpoint<Command1, Result1>(GraphQLMutation = "mutation1")]
      public class Command1 : ICommand { }

      [CommandEndpoint<Command2, Result2>(GraphQLMutation = "mutation2")]
      public class Command2 : ICommand { }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    await Assert.That(errors).IsEmpty();

    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("Command1Mutation");
    await Assert.That(code).Contains("Command2Mutation");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_IncludesAutoGeneratedHeaderAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class OrderResult { }

      [CommandEndpoint<TestCommand, OrderResult>(GraphQLMutation = "test")]
      public class TestCommand : ICommand { }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("<auto-generated/>");
    await Assert.That(code).Contains("DO NOT EDIT");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_IncludesNullableEnableAsync() {
    // Arrange
    var source = """
      using Whizbang.Core;
      using Whizbang.Transports.Mutations;

      namespace TestApp;

      public class OrderResult { }

      [CommandEndpoint<TestCommand, OrderResult>(GraphQLMutation = "test")]
      public class TestCommand : ICommand { }
      """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangGraphQLMutations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("#nullable enable");
  }
}
