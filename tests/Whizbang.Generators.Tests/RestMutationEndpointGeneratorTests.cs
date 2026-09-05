using Whizbang.Transports.FastEndpoints.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the REST mutation endpoint generator.
/// </summary>
/// <remarks>
/// This generator decides which commands get an HTTP surface. Both of its failure directions
/// are silent: generating nothing means a mutation endpoint that a consumer expects simply does
/// not exist, and generating too eagerly would publish a command over HTTP that was deliberately
/// scoped to GraphQL. Neither shows up as a build error, so both are asserted here.
/// </remarks>
/// <tests>Whizbang.Transports.FastEndpoints.Generators/RestMutationEndpointGenerator.cs:*</tests>
public class RestMutationEndpointGeneratorTests {

  private const string MUTATION_STUB = """
    namespace Whizbang.Core {
      public interface IMessage { }
      public interface ICommand : IMessage { }
    }
    namespace Whizbang.Transports.Mutations {
      [System.AttributeUsage(System.AttributeTargets.Class)]
      public sealed class CommandEndpointAttribute<TCommand, TResult> : System.Attribute
          where TCommand : Whizbang.Core.ICommand {
        public string? RestRoute { get; set; }
        public string? GraphQLMutation { get; set; }
        public System.Type? RequestType { get; set; }
      }
    }
    namespace App {
      using Whizbang.Core;
      public record PlaceOrder(string Id) : ICommand;
      public record OrderResult(string Id);
      public class PlaceOrderRequest { }
    }
    """;

  private static string _generatedSource(string declaration) {
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(
      MUTATION_STUB + "\n" + declaration);
    return string.Concat(result.Results
      .SelectMany(r => r.GeneratedSources)
      .Select(s => s.SourceText.ToString()));
  }

  [Test]
  public async Task Generator_CommandWithARestRoute_EmitsAnEndpointAsync() {
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Transports.Mutations;

        [CommandEndpoint<PlaceOrder, OrderResult>(RestRoute = "/api/orders")]
        public class PlaceOrderHandler { }
      }
      """);

    await Assert.That(generated).IsNotEmpty();
    await Assert.That(generated).Contains("/api/orders");
    await Assert.That(generated).Contains("PlaceOrderHandlerEndpoint")
      .Because("the endpoint class name is derived from the annotated type and is what gets registered");
  }

  [Test]
  public async Task Generator_WithoutARestRoute_EmitsNothingAsync() {
    // The deliberate one. A command endpoint with no REST route is scoped to GraphQL, so
    // generating for it anyway would publish a mutation over HTTP that nobody chose to expose --
    // and it would look like a feature rather than a leak.
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Transports.Mutations;

        [CommandEndpoint<PlaceOrder, OrderResult>(GraphQLMutation = "placeOrder")]
        public class PlaceOrderHandler { }
      }
      """);

    await Assert.That(generated).IsEmpty()
      .Because("a GraphQL-only command must not acquire an HTTP surface by omission");
  }

  [Test]
  public async Task Generator_WithAnEmptyRestRoute_EmitsNothingAsync() {
    // Empty is treated the same as absent. Emitting here would register an endpoint on "", which
    // collides with every other route that made the same mistake.
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Transports.Mutations;

        [CommandEndpoint<PlaceOrder, OrderResult>(RestRoute = "")]
        public class PlaceOrderHandler { }
      }
      """);

    await Assert.That(generated).IsEmpty();
  }

  [Test]
  public async Task Generator_WithoutTheAttribute_EmitsNothingAsync() {
    var generated = _generatedSource("""
      namespace App {
        public class PlaceOrderHandler { }
      }
      """);

    await Assert.That(generated).IsEmpty();
  }

  [Test]
  public async Task Generator_CarriesBothTypeArgumentsIntoTheEndpointAsync() {
    // The command and result types are the endpoint's request and response contract. Losing
    // either would produce source that does not compile, or an endpoint bound to the wrong type.
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Transports.Mutations;

        [CommandEndpoint<PlaceOrder, OrderResult>(RestRoute = "/api/orders")]
        public class PlaceOrderHandler { }
      }
      """);

    await Assert.That(generated).Contains("PlaceOrder");
    await Assert.That(generated).Contains("OrderResult");
  }

  [Test]
  public async Task Generator_WithAnExplicitRequestType_UsesItAsync() {
    // RequestType lets a consumer bind a purpose-built DTO instead of the command itself, which
    // is how they keep transport shape out of the domain type.
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Transports.Mutations;

        [CommandEndpoint<PlaceOrder, OrderResult>(RestRoute = "/api/orders", RequestType = typeof(PlaceOrderRequest))]
        public class PlaceOrderHandler { }
      }
      """);

    await Assert.That(generated).Contains("PlaceOrderRequest");
  }

  [Test]
  public async Task Generator_EmitsIntoTheExpectedFileAsync() {
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(
      MUTATION_STUB + """
      namespace App {
        using Whizbang.Transports.Mutations;

        [CommandEndpoint<PlaceOrder, OrderResult>(RestRoute = "/api/orders")]
        public class PlaceOrderHandler { }
      }
      """);

    var hints = result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.HintName).ToList();
    await Assert.That(hints).Contains("WhizbangRestMutationEndpoints.g.cs");
  }

  [Test]
  public async Task Generator_OnAnEmptyCompilation_EmitsNothingWithoutErrorsAsync() {
    var result = GeneratorTestHelper.RunGenerator<RestMutationEndpointGenerator>(
      "namespace App { public class Nothing { } }");

    await Assert.That(result.Results.SelectMany(r => r.GeneratedSources)).IsEmpty();
    await Assert.That(result.Diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
      .IsFalse();
  }
}
