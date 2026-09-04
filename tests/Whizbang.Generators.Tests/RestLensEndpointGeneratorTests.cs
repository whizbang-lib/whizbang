using Whizbang.Transports.FastEndpoints.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the REST lens endpoint generator.
/// </summary>
/// <remarks>
/// A source generator fails by producing nothing. There is no exception and no build error --
/// the consumer's REST endpoint simply does not exist, and the first sign of it is a 404 in an
/// environment where someone expected an API. So these tests assert both directions: what the
/// generator emits for a valid declaration, and that it stays silent for declarations it cannot
/// legitimately serve.
/// </remarks>
/// <tests>Whizbang.Transports.FastEndpoints.Generators/RestLensEndpointGenerator.cs:*</tests>
public class RestLensEndpointGeneratorTests {

  private const string LENS_QUERY_STUB = """
    namespace Whizbang.Core.Lenses {
      public interface ILensQuery { }
      public interface ILensQuery<TModel> : ILensQuery where TModel : class { }
    }
    namespace Whizbang.Transports.FastEndpoints {
      [System.AttributeUsage(System.AttributeTargets.Interface | System.AttributeTargets.Class)]
      public sealed class RestLensAttribute : System.Attribute {
        public string? Route { get; set; }
        public bool EnableFiltering { get; set; } = true;
        public bool EnableSorting { get; set; } = true;
        public bool EnablePaging { get; set; } = true;
        public int DefaultPageSize { get; set; } = 10;
        public int MaxPageSize { get; set; } = 100;
      }
    }
    namespace App {
      public class Order { }
    }
    """;

  private static string _generatedSource(string declaration) {
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(
      LENS_QUERY_STUB + "\n" + declaration);
    return string.Concat(result.Results
      .SelectMany(r => r.GeneratedSources)
      .Select(s => s.SourceText.ToString()));
  }

  [Test]
  public async Task Generator_LensWithTheAttribute_EmitsAnEndpointAsync() {
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Core.Lenses;
        using Whizbang.Transports.FastEndpoints;

        [RestLens(Route = "/api/orders")]
        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    await Assert.That(generated).IsNotEmpty()
      .Because("a lens marked [RestLens] is the entire trigger for generating its endpoint");
    await Assert.That(generated).Contains("/api/orders");
  }

  [Test]
  public async Task Generator_WithoutTheAttribute_EmitsNothingAsync() {
    // A lens is an ordinary query type until someone opts it into HTTP. Generating an endpoint
    // for every lens would publish query surfaces nobody asked to expose.
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Core.Lenses;

        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    await Assert.That(generated).IsEmpty();
  }

  [Test]
  public async Task Generator_AttributeWithoutILensQuery_EmitsNothingAsync() {
    // The model type comes from ILensQuery<TModel>. Without it there is nothing to bind the
    // endpoint to, so emitting anyway would produce source that cannot compile -- which is a
    // worse outcome than declining, because it breaks the whole build rather than one route.
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Transports.FastEndpoints;

        [RestLens(Route = "/api/orders")]
        public interface INotALens { }
      }
      """);

    await Assert.That(generated).IsEmpty();
  }

  [Test]
  public async Task Generator_WithoutAnExplicitRoute_DerivesOneFromTheModelAsync() {
    // Route is optional, so the fallback is what most consumers actually get. If it silently
    // produced an empty route, every such endpoint would collide at "/".
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Core.Lenses;
        using Whizbang.Transports.FastEndpoints;

        [RestLens]
        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    await Assert.That(generated).IsNotEmpty();
    await Assert.That(generated).Contains("/api/order")
      .Because("the default route is derived from the model type name, not left blank");
  }

  [Test]
  public async Task Generator_CarriesThePagingBoundsIntoTheEndpointAsync() {
    // MaxPageSize is a denial-of-service guard: it caps what a caller can request. A generator
    // that dropped it would emit an endpoint that honors any page size a client asks for.
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Core.Lenses;
        using Whizbang.Transports.FastEndpoints;

        [RestLens(Route = "/api/orders", DefaultPageSize = 25, MaxPageSize = 250)]
        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    await Assert.That(generated).Contains("25");
    await Assert.That(generated).Contains("250");
  }

  [Test]
  public async Task Generator_EmitsIntoTheExpectedFileAsync() {
    // The file name is the handle anyone debugging generated output looks for.
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(
      LENS_QUERY_STUB + """
      namespace App {
        using Whizbang.Core.Lenses;
        using Whizbang.Transports.FastEndpoints;

        [RestLens(Route = "/api/orders")]
        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    var hints = result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.HintName).ToList();
    await Assert.That(hints).Contains("WhizbangRestLensEndpoints.g.cs");
  }

  [Test]
  public async Task Generator_OnAnEmptyCompilation_EmitsNothingAndDoesNotThrowAsync() {
    // Most projects contain no lenses at all. The generator runs on every one of them.
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(
      "namespace App { public class Nothing { } }");

    await Assert.That(result.Results.SelectMany(r => r.GeneratedSources)).IsEmpty();
    await Assert.That(result.Diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
      .IsFalse();
  }
}
