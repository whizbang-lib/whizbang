using Whizbang.Transports.HotChocolate.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the HotChocolate GraphQL generators.
/// </summary>
/// <remarks>
/// One attribute, <c>[CommandEndpoint&lt;TCommand, TResult&gt;]</c>, drives two independent
/// generators: the REST one keys on <c>RestRoute</c>, this one on <c>GraphQLMutation</c>. Each
/// gate decides whether a command is exposed on that transport, so a regression in either does
/// not fail a build -- it cross-exposes a mutation on a protocol it was never scoped to. The
/// gates are therefore asserted from both sides here.
/// </remarks>
/// <tests>Whizbang.Transports.HotChocolate.Generators/GraphQLMutationTypeGenerator.cs:*</tests>
/// <tests>Whizbang.Transports.HotChocolate.Generators/GraphQLLensTypeGenerator.cs:*</tests>
public class GraphQLGeneratorTests {

  private const string STUB = """
    namespace Whizbang.Core {
      public interface IMessage { }
      public interface ICommand : IMessage { }
    }
    namespace Whizbang.Core.Lenses {
      public interface ILensQuery { }
      public interface ILensQuery<TModel> : ILensQuery where TModel : class { }
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
    namespace Whizbang.Transports.HotChocolate {
      [System.AttributeUsage(System.AttributeTargets.Interface | System.AttributeTargets.Class)]
      public sealed class GraphQLLensAttribute : System.Attribute {
        public string? QueryName { get; set; }
        public int Scope { get; set; }
        public bool EnableFiltering { get; set; } = true;
        public bool EnableSorting { get; set; } = true;
        public bool EnablePaging { get; set; } = true;
        public bool EnableProjection { get; set; } = true;
      }
    }
    namespace App {
      using Whizbang.Core;
      public record PlaceOrder(string Id) : ICommand;
      public record OrderResult(string Id);
      public class Order { }
    }
    """;

  private static string _mutationOutput(string declaration) {
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(STUB + "\n" + declaration);
    return string.Concat(result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));
  }

  private static string _lensOutput(string declaration) {
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(STUB + "\n" + declaration);
    return string.Concat(result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));
  }

  // ── Mutation generator ────────────────────────────────────────────────────

  [Test]
  public async Task Mutation_WithAGraphQLName_EmitsTheMutationAsync() {
    var generated = _mutationOutput("""
      namespace App {
        using Whizbang.Transports.Mutations;

        [CommandEndpoint<PlaceOrder, OrderResult>(GraphQLMutation = "placeOrder")]
        public class PlaceOrderHandler { }
      }
      """);

    await Assert.That(generated).IsNotEmpty();
    await Assert.That(generated).Contains("placeOrder");
    await Assert.That(generated).Contains("PlaceOrder");
    await Assert.That(generated).Contains("OrderResult");
  }

  [Test]
  public async Task Mutation_RestOnlyCommand_EmitsNothingAsync() {
    // The gate that keeps the two transports independent. A command scoped to REST must not
    // appear in the GraphQL schema, and nothing about that failure would break a build -- the
    // mutation would simply become callable over a protocol nobody chose for it.
    var generated = _mutationOutput("""
      namespace App {
        using Whizbang.Transports.Mutations;

        [CommandEndpoint<PlaceOrder, OrderResult>(RestRoute = "/api/orders")]
        public class PlaceOrderHandler { }
      }
      """);

    await Assert.That(generated).IsEmpty()
      .Because("a REST-scoped command must not acquire a GraphQL mutation by omission");
  }

  [Test]
  public async Task Mutation_ExposedOnBothTransports_EmitsForEachAsync() {
    // Both properties set is the legitimate dual-exposure case, and it proves the two gates are
    // independent rather than mutually exclusive.
    const string decl = """
      namespace App {
        using Whizbang.Transports.Mutations;

        [CommandEndpoint<PlaceOrder, OrderResult>(RestRoute = "/api/orders", GraphQLMutation = "placeOrder")]
        public class PlaceOrderHandler { }
      }
      """;

    await Assert.That(_mutationOutput(decl)).Contains("placeOrder");
  }

  [Test]
  public async Task Mutation_EmptyGraphQLName_EmitsNothingAsync() {
    var generated = _mutationOutput("""
      namespace App {
        using Whizbang.Transports.Mutations;

        [CommandEndpoint<PlaceOrder, OrderResult>(GraphQLMutation = "")]
        public class PlaceOrderHandler { }
      }
      """);

    await Assert.That(generated).IsEmpty();
  }

  [Test]
  public async Task Mutation_EmitsIntoTheExpectedFileAsync() {
    var result = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(STUB + """
      namespace App {
        using Whizbang.Transports.Mutations;

        [CommandEndpoint<PlaceOrder, OrderResult>(GraphQLMutation = "placeOrder")]
        public class PlaceOrderHandler { }
      }
      """);

    var hints = result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.HintName).ToList();
    await Assert.That(hints).Contains("WhizbangGraphQLMutations.g.cs");
  }

  // ── Lens generator ────────────────────────────────────────────────────────

  [Test]
  public async Task Lens_WithTheAttribute_EmitsAQueryAsync() {
    var generated = _lensOutput("""
      namespace App {
        using Whizbang.Core.Lenses;
        using Whizbang.Transports.HotChocolate;

        [GraphQLLens(QueryName = "orders")]
        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    await Assert.That(generated).IsNotEmpty();
    await Assert.That(generated).Contains("orders");
  }

  [Test]
  public async Task Lens_WithoutILensQuery_EmitsNothingAsync() {
    // The model type comes from ILensQuery<TModel>; without it there is nothing to query, and
    // emitting anyway would produce source that cannot compile.
    var generated = _lensOutput("""
      namespace App {
        using Whizbang.Transports.HotChocolate;

        [GraphQLLens(QueryName = "orders")]
        public interface INotALens { }
      }
      """);

    await Assert.That(generated).IsEmpty();
  }

  [Test]
  public async Task Lens_WithoutTheAttribute_EmitsNothingAsync() {
    // Lenses are ordinary query types until someone opts them into the schema. Generating for
    // all of them would publish read surfaces nobody chose to expose.
    var generated = _lensOutput("""
      namespace App {
        using Whizbang.Core.Lenses;

        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    await Assert.That(generated).IsEmpty();
  }

  [Test]
  public async Task Lens_EmitsIntoTheExpectedFileAsync() {
    var result = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(STUB + """
      namespace App {
        using Whizbang.Core.Lenses;
        using Whizbang.Transports.HotChocolate;

        [GraphQLLens(QueryName = "orders")]
        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    var hints = result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.HintName).ToList();
    await Assert.That(hints).Contains("WhizbangLensQueries.g.cs");
  }

  [Test]
  public async Task BothGenerators_OnAnEmptyCompilation_StaySilentAsync() {
    // They run on every project in a solution, most of which contain neither lenses nor
    // command endpoints.
    const string empty = "namespace App { public class Nothing { } }";

    var mutation = GeneratorTestHelper.RunGenerator<GraphQLMutationTypeGenerator>(empty);
    var lens = GeneratorTestHelper.RunGenerator<GraphQLLensTypeGenerator>(empty);

    await Assert.That(mutation.Results.SelectMany(r => r.GeneratedSources)).IsEmpty();
    await Assert.That(lens.Results.SelectMany(r => r.GeneratedSources)).IsEmpty();
    await Assert.That(mutation.Diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsFalse();
    await Assert.That(lens.Diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsFalse();
  }
}
