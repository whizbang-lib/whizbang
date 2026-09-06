using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="PerspectiveDiscoveryGenerator"/>, complementing the large
/// <c>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs</c> suite. These target
/// two "not a named type" guards that are reachable from arbitrary user-declared type arguments (an
/// array <c>TModel</c>, and a jagged-array <c>TEvent</c>) rather than anything the generator itself
/// produces.
/// </summary>
/// <remarks>
/// Two of the round's targets in this file are NOT covered here, because tracing their only caller
/// shows them unreachable in a compiling program:
/// <list type="bullet">
/// <item><c>_extractStreamIdProperty</c>'s <c>typeToExtract is not INamedTypeSymbol =&gt; return
/// null</c> (line 243). Its only call site, in <c>_validateAndExtractEventInfo</c>, invokes it on a
/// given <c>eventTypeSymbol</c> ONLY when <c>_validateEventStreamId</c> already returned no error for
/// that exact same symbol — which requires that method's identical array-unwrap-then-named-type-check
/// to have already succeeded. Both methods perform the same unwrap on the same input, so by the time
/// <c>_extractStreamIdProperty</c> runs, the type is guaranteed already-named.</item>
/// <item><c>_generateTypedAssociations</c>'s <c>if (perspectives.IsEmpty) return
/// "return Array.Empty&lt;...&gt;();"</c> (line 399). Its only caller, <c>_generateRegistrationSource</c>,
/// is itself only invoked from <c>_generatePerspectiveRegistrations</c> after that method has already
/// returned early on an empty <c>perspectives</c> array — so <c>_generateTypedAssociations</c> is never
/// reached with an empty array in the real pipeline.</item>
/// </list>
/// </remarks>
public class PerspectiveDiscoveryGeneratorCoverageTests {

  // TModel being an array type (string[]) is not an INamedTypeSymbol, so _findStreamIdProperty's
  // guard (PerspectiveDiscoveryGenerator.cs:156) returns null instead of an invalid cast. If this
  // guard regressed, an author who (accidentally or intentionally) models a perspective's read model
  // as an array would crash the whole source-generation pass instead of just registering the
  // perspective without stream-id metadata.
  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_ArrayModelType_RegistersWithoutCrashingAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace {
        public record OrderCreatedEvent : IEvent {
          [StreamId]
          public string OrderId { get; init; } = "";
        }

        // TModel is an array type: not an INamedTypeSymbol, exercising the defensive guard in
        // _findStreamIdProperty rather than an invalid cast to INamedTypeSymbol.
        public class ArrayModelPerspective : IPerspectiveFor<string[], OrderCreatedEvent> {
          public string[] Apply(string[] currentData, OrderCreatedEvent @event) {
            return currentData;
          }
        }
      }
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty()
      .Because("an array TModel must not fail source generation, only skip model-level StreamId lookup");

    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("ArrayModelPerspective")
      .Because("the perspective must still be registered even though its model type is an array");

    var whiz007 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ007");
    await Assert.That(whiz007).IsNotNull();
    await Assert.That(whiz007!.GetMessage(CultureInfo.InvariantCulture)).Contains("ArrayModelPerspective");
  }

  // A jagged-array event type (OrderEvent[][]) unwraps ONE array level (the generator's only
  // supported array shape) to OrderEvent[] — itself still an array, so
  // _validateEventStreamId's "not a named type" guard (PerspectiveDiscoveryGenerator.cs:206-207)
  // fires, rather than the property-count check further down. If this guard were missing, a
  // jagged-array event would either crash the generator (invalid cast) or be silently treated as
}
