using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Direct coverage tests for <c>Whizbang.Generators.CompileTimeMessageClassification</c> — the
/// compile-time mirror of <c>Whizbang.Core.Routing.MessageKindDetector</c>'s priority rules used by
/// the receptor registry generator and the WHIZ151 ownership analyzer.
/// </summary>
/// <remarks>
/// <c>CompileTimeMessageClassification</c> and its members are declared <c>internal</c>, and this
/// project deliberately does not use <c>InternalsVisibleTo</c> for the generators assembly (see
/// <c>src/Whizbang.Generators/AssemblyInfo.cs</c> — it conflicts with PolySharp polyfills), so these
/// tests reach the type via reflection instead of a direct reference. Because
/// <c>DetectMessageKind</c>/<c>FireAtStagesOf</c> compare attribute names and interface names as
/// plain strings (that's the whole point of a "mirror" — it does not depend on the real
/// <c>Whizbang.Core</c> assembly being loaded), several tests below declare their own
/// same-named-and-namespaced stand-in attributes/interfaces in an isolated compilation rather than
/// referencing the real Whizbang.Core types. That is the only way to reach a "matched the name, but
/// the shape is wrong" branch: the real attributes require a constructor argument, so no valid use
/// of the real types can ever have zero constructor arguments.
/// </remarks>
public class CompileTimeMessageClassificationCoverageTests {
  private static readonly Type _classificationType = typeof(AttributeArgNamingHelper).Assembly
    .GetType("Whizbang.Generators.CompileTimeMessageClassification")
    ?? throw new InvalidOperationException("Whizbang.Generators.CompileTimeMessageClassification not found — check the type's namespace/name.");

  private static string _detectMessageKind(ITypeSymbol type) {
    var method = _classificationType.GetMethod("DetectMessageKind", BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException("CompileTimeMessageClassification.DetectMessageKind(ITypeSymbol) not found.");
    return (string)method.Invoke(null, [type])!;
  }

  private static ImmutableArray<string> _fireAtStagesOf(INamedTypeSymbol receptorClass) {
    var method = _classificationType.GetMethod("FireAtStagesOf", BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException("CompileTimeMessageClassification.FireAtStagesOf(INamedTypeSymbol) not found.");
    return (ImmutableArray<string>)method.Invoke(null, [receptorClass])!;
  }

  private static INamedTypeSymbol _classSymbolFor(string source, string typeName) {
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var tree = compilation.SyntaxTrees.Single();
    var model = compilation.GetSemanticModel(tree);
    var declaration = tree.GetRoot().DescendantNodes()
      .OfType<ClassDeclarationSyntax>()
      .First(c => c.Identifier.Text == typeName);
    return (INamedTypeSymbol)model.GetDeclaredSymbol(declaration)!;
  }

  // ==================== DetectMessageKind: Priority-1 attribute edge cases ====================

  [Test]
  public async Task DetectMessageKind_MatchingAttributeWithNoConstructorArguments_FallsThroughAsync() {
    // A [MessageKind] application that bound to the right attribute class but carries no
    // constructor argument (a stale attribute definition mismatch, or mid-edit incomplete code)
    // must not be treated as an override with some unknown value — it must be skipped so later
    // priorities (interface, namespace, suffix) still get a chance to classify the type correctly.
    const string source = """
      namespace Whizbang.Core.Routing {
        public class MessageKindAttribute : System.Attribute { }
      }
      namespace TestNamespace {
        [Whizbang.Core.Routing.MessageKind]
        public class Widget { }
      }
      """;
    var symbol = _classSymbolFor(source, "Widget");

    var kind = _detectMessageKind(symbol);

    await Assert.That(kind).IsEqualTo("Unknown")
      .Because("an attribute application with no bound constructor argument carries no usable override value");
  }

  [Test]
  public async Task DetectMessageKind_AttributeArgumentValueMatchesNoEnumMember_FallsThroughAsync() {
    // An explicit [MessageKind] value that does not correspond to any declared enum member (an
    // out-of-range cast, or a member renamed/removed since the attribute was applied) must not
    // silently resolve to an arbitrary name — the classifier must fall through to the next
    // priority instead of fabricating a kind nobody actually declared.
    const string source = """
      namespace Whizbang.Core.Routing {
        public enum FakeMessageKind { A = 0, B = 1 }
        public class MessageKindAttribute : System.Attribute {
          public MessageKindAttribute(FakeMessageKind kind) { }
        }
      }
      namespace TestNamespace {
        [Whizbang.Core.Routing.MessageKind((Whizbang.Core.Routing.FakeMessageKind)999)]
        public class Widget { }
      }
      """;
    var symbol = _classSymbolFor(source, "Widget");

    var kind = _detectMessageKind(symbol);

    await Assert.That(kind).IsEqualTo("Unknown")
      .Because("an out-of-range attribute argument value matches no enum member and must not be reported as a kind");
  }

  // ==================== DetectMessageKind: Priority-3 marker interfaces ====================

  [Test]
  public async Task DetectMessageKind_TypeImplementsQueryMarkerInterface_ReturnsQueryAsync() {
    // A type whose only classification signal is the IQuery marker interface must resolve to
    // Query — if this branch regresses, every query routed purely by interface (no attribute, no
    // naming convention) silently loses its query routing and query-response semantics.
    const string source = """
      namespace Whizbang.Core {
        public interface IQuery { }
      }
      namespace TestNamespace {
        public class Widget : Whizbang.Core.IQuery { }
      }
      """;
    var symbol = _classSymbolFor(source, "Widget");

    var kind = _detectMessageKind(symbol);

    await Assert.That(kind).IsEqualTo("Query")
      .Because("implementing Whizbang.Core.IQuery is priority-3 evidence and must classify the type as Query");
  }

  // ==================== FireAtStagesOf: attribute walk edge cases ====================

  [Test]
  public async Task FireAtStagesOf_ClassHasAnUnrelatedAttribute_SkipsItAsync() {
    // A receptor class may carry attributes that have nothing to do with lifecycle stage
    // placement (e.g. [Obsolete]). The stage walk must skip those rather than mistake one for a
    // mis-shaped [FireAt] and either crash or report a bogus stage.
    const string source = """
      namespace TestNamespace {
        [System.Obsolete]
        public class WidgetReceptor { }
      }
      """;
    var symbol = _classSymbolFor(source, "WidgetReceptor");

    var stages = _fireAtStagesOf(symbol);

    await Assert.That(stages).IsEmpty()
      .Because("an attribute unrelated to [FireAt] must not contribute a lifecycle stage");
  }

  [Test]
  public async Task FireAtStagesOf_MatchingAttributeWithNoConstructorArguments_SkipsItAsync() {
    // A [FireAt] application that bound to the right attribute class but carries no stage
    // argument (a stale attribute definition mismatch, or mid-edit incomplete code) must not be
    // treated as naming some default stage — skipping it is the only safe outcome, or a receptor
    // could be silently misclassified as a lifecycle hook (or an inbox handler) it never declared.
    const string source = """
      namespace Whizbang.Core.Messaging {
        public class FireAtAttribute : System.Attribute { }
      }
      namespace TestNamespace {
        [Whizbang.Core.Messaging.FireAt]
        public class WidgetReceptor { }
      }
      """;
    var symbol = _classSymbolFor(source, "WidgetReceptor");

    var stages = _fireAtStagesOf(symbol);

    await Assert.That(stages).IsEmpty()
      .Because("an attribute application with no bound constructor argument carries no usable stage");
  }
}
