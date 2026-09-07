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
/// array <c>TModel</c>, and an open type-parameter <c>TEvent</c>) rather than anything the generator
/// itself produces.
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

  // A perspective that stays OPEN over its event type hands the validator an
  // ITypeParameterSymbol, not a named type — there is no declaration to look for [StreamId] on, and
  // no way to know at generation time which concrete events will be substituted. The guard has to
  // record that as a validation error (PerspectiveDiscoveryGenerator.cs:206-207) rather than cast;
  // without it the whole perspective-registration pass throws and EVERY perspective in the assembly
  // loses its registration, not just the open-generic one. The error is what tells the author the
  // open perspective cannot be stream-keyed — silently accepting it would register a perspective
  // whose rows have no stream to fold against.
  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_OpenGenericEventType_ReportsMissingStreamIdAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace {
        public record OrderCreatedEvent : IEvent {
          [StreamId]
          public string OrderId { get; init; } = "";
        }

        public class OrderModel {
          [StreamId]
          public string OrderId { get; set; } = "";
        }

        // TEvent is never substituted here, so the generator sees the type PARAMETER itself.
        public class OpenEventPerspective<TEvent> : IPerspectiveFor<OrderModel, TEvent> where TEvent : IEvent {
          public OrderModel Apply(OrderModel currentData, TEvent @event) {
            return currentData;
          }
        }

        // A closed sibling in the same compilation: it must survive, which is only observable if
        // the open one was declined rather than allowed to throw.
        public class ClosedEventPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
          public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
            return currentData;
          }
        }
      }
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    var missingStreamId = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ030");
    await Assert.That(missingStreamId).IsNotNull()
      .Because("an event type that is still an open type parameter has no discoverable [StreamId] and must be reported, not accepted");

    var message = missingStreamId!.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("TEvent")
      .Because("the message must name the unresolved type parameter so the author sees which type argument is the problem");
    await Assert.That(message).Contains("OpenEventPerspective")
      .Because("the message must name the perspective that declared it, not the closed sibling");

    await Assert.That(result.Diagnostics.Count(d => d.Id == "WHIZ030")).IsEqualTo(1)
      .Because("only the open perspective may fail validation; the closed sibling's event carries [StreamId]");

    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull()
      .Because("the registration pass must still complete — a throw here would take every perspective in the assembly with it");
    await Assert.That(generatedSource).Contains("ClosedEventPerspective")
      .Because("the closed sibling must still be registered alongside the declined open one");
  }
}
