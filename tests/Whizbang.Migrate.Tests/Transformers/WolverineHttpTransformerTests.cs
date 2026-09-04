using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Tests for the Wolverine HTTP to FastEndpoints transformer.
/// </summary>
/// <remarks>
/// This transformer deletes routing attributes. That is necessary -- they will not compile once
/// the Wolverine package is gone -- but it means a silent bug here removes a service's HTTP
/// endpoint and leaves nothing behind saying so. The endpoint would simply stop existing, with a
/// clean build. So the tests treat the TODO comment and the warning as load-bearing output, not
/// cosmetic: they are the only trace that a route once lived there.
/// </remarks>
/// <tests>Whizbang.Migrate/Transformers/WolverineHttpTransformer.cs:*</tests>
public class WolverineHttpTransformerTests {

  private const string ORDER_ENDPOINT = """
    using Wolverine.Http;

    public class OrderEndpoints {
      [WolverineGet("/orders/{id}")]
      public string GetOrder(string id) => id;
    }
    """;
  [Test]
  public async Task TransformAsync_FileWithoutWolverineHttp_IsLeftByteForByteAsync() {
    // The migrator sweeps a whole solution; a file that never used Wolverine HTTP must not be
    // rewritten, or the tool produces diff noise in files it had no business touching.
    var transformer = new WolverineHttpTransformer();
    const string source = """
      using System;

      public class OrderService {
        public string Get(string id) => id;
      }
      """;

    var result = await transformer.TransformAsync(source, "OrderService.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source);
    await Assert.That(result.Changes).IsEmpty();
    await Assert.That(result.Warnings).IsEmpty();
  }

  [Test]
  public async Task TransformAsync_RouteAttribute_IsRemovedSoTheFileCompilesAsync() {
    // The attribute cannot survive: without the Wolverine package it is an unresolved type. This
    // is the transformer's one destructive act, and the reason the TODO below has to exist.
    var transformer = new WolverineHttpTransformer();

    var result = await transformer.TransformAsync(ORDER_ENDPOINT, "OrderEndpoints.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("WolverineGet");
  }

  [Test]
  public async Task TransformAsync_RemovedRoute_LeavesATodoNamingMethodAndPathAsync() {
    // The replacement trace. Losing this turns endpoint deletion into a silent one -- the build
    // stays green and the route is simply gone, which is the worst outcome this tool can produce.
    var transformer = new WolverineHttpTransformer();

    var result = await transformer.TransformAsync(ORDER_ENDPOINT, "OrderEndpoints.cs");

    await Assert.That(result.TransformedCode).Contains("TODO");
    await Assert.That(result.TransformedCode).Contains("GET")
      .Because("the HTTP verb is derived from the attribute name and must survive its removal");
    await Assert.That(result.TransformedCode).Contains("/orders/{id}")
      .Because("the route is the piece a human needs to rebuild the endpoint");
  }

  [Test]
  public async Task TransformAsync_RemovedRoute_WarnsWithClassAndMethodAsync() {
    // The warning is what surfaces in the migration report, where an operator reviews everything
    // the tool could not finish. A route removed without one is invisible.
    var transformer = new WolverineHttpTransformer();

    var result = await transformer.TransformAsync(ORDER_ENDPOINT, "OrderEndpoints.cs");

    var warning = result.Warnings.FirstOrDefault(w => w.Contains("MANUAL CONVERSION", StringComparison.Ordinal));
    await Assert.That(warning).IsNotNull();
    await Assert.That(warning!).Contains("OrderEndpoints.GetOrder()");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.AttributeRemoved)).IsTrue();
  }

  [Test]
  [Arguments("WolverineGet", "GET")]
  [Arguments("WolverinePost", "POST")]
  [Arguments("WolverineDelete", "DELETE")]
  [Arguments("WolverinePut", "PUT")]
  [Arguments("WolverinePatch", "PATCH")]
  public async Task TransformAsync_EveryVerb_IsCarriedIntoTheTodoAsync(string attribute, string verb) {
    // The verb is recovered by trimming the "Wolverine" prefix. A mapping that silently produced
    // the wrong verb would send someone rebuilding a POST endpoint as a GET.
    var transformer = new WolverineHttpTransformer();
    var source = $$"""
      using Wolverine.Http;

      public class Endpoints {
        [{{attribute}}("/thing")]
        public string Handle() => "x";
      }
      """;

    var result = await transformer.TransformAsync(source, "Endpoints.cs");

    await Assert.That(result.TransformedCode).Contains($"{verb}(\"/thing\")");
    await Assert.That(result.TransformedCode).DoesNotContain(attribute);
  }

  [Test]
  public async Task TransformAsync_WolverineUsing_BecomesFastEndpointsPlusWhizbangAsync() {
    // Both usings are required: FastEndpoints for the base types, and the Whizbang integration
    // for the wiring. Emitting only the first leaves code that looks migrated but is not.
    var transformer = new WolverineHttpTransformer();

    var result = await transformer.TransformAsync(ORDER_ENDPOINT, "OrderEndpoints.cs");

    await Assert.That(result.TransformedCode).Contains("using FastEndpoints;");
    await Assert.That(result.TransformedCode).Contains("using Whizbang.Transports.FastEndpoints;");
    await Assert.That(result.TransformedCode).DoesNotContain("using Wolverine.Http;");
  }

  [Test]
  public async Task TransformAsync_BothWolverineUsings_DoNotDuplicateTheReplacementsAsync() {
    // A file importing both spellings must not end up with two copies of each replacement using,
    // which would not compile.
    var transformer = new WolverineHttpTransformer();
    const string source = """
      using Wolverine.Http;
      using WolverineFx.Http;

      public class Endpoints {
        [WolverineGet("/a")]
        public string A() => "a";
      }
      """;

    var result = await transformer.TransformAsync(source, "Endpoints.cs");

    var fastEndpointsCount = result.TransformedCode.Split("using FastEndpoints;").Length - 1;
    var whizbangCount = result.TransformedCode.Split("using Whizbang.Transports.FastEndpoints;").Length - 1;
    await Assert.That(fastEndpointsCount).IsEqualTo(1);
    await Assert.That(whizbangCount).IsEqualTo(1);
  }

  [Test]
  public async Task TransformAsync_UnrelatedUsings_ArePreservedAsync() {
    var transformer = new WolverineHttpTransformer();
    const string source = """
      using System;
      using Wolverine.Http;
      using System.Text.Json;

      public class Endpoints {
        [WolverinePost("/a")]
        public string A() => "a";
      }
      """;

    var result = await transformer.TransformAsync(source, "Endpoints.cs");

    await Assert.That(result.TransformedCode).Contains("using System;");
    await Assert.That(result.TransformedCode).Contains("using System.Text.Json;");
  }

  [Test]
  public async Task TransformAsync_NonWolverineAttributesOnTheSameMethod_SurviveAsync() {
    // Only the Wolverine attribute is unresolvable. Stripping the whole attribute list would
    // quietly drop behavior the migration was never asked to touch.
    var transformer = new WolverineHttpTransformer();
    const string source = """
      using System;
      using Wolverine.Http;

      public class Endpoints {
        [Obsolete("old")]
        [WolverineGet("/a")]
        public string A() => "a";
      }
      """;

    var result = await transformer.TransformAsync(source, "Endpoints.cs");

    await Assert.That(result.TransformedCode).Contains("Obsolete(\"old\")")
      .Because("attributes unrelated to Wolverine are not this transformer's to remove");
    await Assert.That(result.TransformedCode).DoesNotContain("WolverineGet");
  }

  [Test]
  public async Task TransformAsync_AttributeWithNoRoute_FallsBackToRootAsync() {
    // A parameterless attribute still needs some route in the TODO, or the reminder reads as
    // though the endpoint had no path at all.
    var transformer = new WolverineHttpTransformer();
    const string source = """
      using Wolverine.Http;

      public class Endpoints {
        [WolverineGet]
        public string A() => "a";
      }
      """;

    var result = await transformer.TransformAsync(source, "Endpoints.cs");

    await Assert.That(result.TransformedCode).Contains("GET(\"/\")");
  }

  [Test]
  public async Task TransformAsync_AttributeWithoutTheUsing_IsStillHandledAsync() {
    // Detection accepts either signal. A file relying on a global using would otherwise sail
    // past untouched and fail to compile after the package is dropped.
    var transformer = new WolverineHttpTransformer();
    const string source = """
      public class Endpoints {
        [WolverinePost("/b")]
        public string B() => "b";
      }
      """;

    var result = await transformer.TransformAsync(source, "Endpoints.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("WolverinePost");
    await Assert.That(result.TransformedCode).Contains("POST(\"/b\")");
  }
}
