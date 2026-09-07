using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Direct coverage tests for <c>Whizbang.Generators.Utilities.TypeNameHelper</c>'s interface
/// enumeration and lookup members (<c>GetImplementedInterfaces</c>, <c>FindInterface</c>). Every
/// other member on this centralized helper is exercised indirectly through
/// <c>ImplementsInterface</c>/<c>ImplementsGenericInterface</c>/<c>FindInterfaceByOriginalDefinition</c>
/// call sites across the generators, but no generator currently calls these two.
/// </summary>
/// <remarks>
/// <c>TypeNameHelper</c> is declared <c>internal</c>, and this project deliberately does not use
/// <c>InternalsVisibleTo</c> for the generators assembly (see
/// <c>src/Whizbang.Generators/AssemblyInfo.cs</c> — it conflicts with PolySharp polyfills). Its
/// members are <c>public</c> though, so a real reference is still blocked at compile time by the
/// containing type's accessibility, but reflection sees them without any special
/// <see cref="System.Reflection.BindingFlags"/>. Building a minimal compilation to obtain a real
/// <see cref="ITypeSymbol"/> and calling the helper directly this way is far cheaper than driving a
/// full generator pipeline just to observe these two methods.
/// </remarks>
public class TypeNameHelperCoverageTests {
  private static readonly Type _typeNameHelperType = typeof(AttributeArgNamingHelper).Assembly
    .GetType("Whizbang.Generators.Utilities.TypeNameHelper")
    ?? throw new InvalidOperationException("Whizbang.Generators.Utilities.TypeNameHelper not found — check the type's namespace/name.");

  private static string[] _getImplementedInterfaces(ITypeSymbol type) {
    var method = _typeNameHelperType.GetMethod("GetImplementedInterfaces")
      ?? throw new InvalidOperationException("TypeNameHelper.GetImplementedInterfaces(ITypeSymbol) not found.");
    return (string[])method.Invoke(null, [type])!;
  }

  private static INamedTypeSymbol? _findInterface(ITypeSymbol type, string interfaceFullName) {
    var method = _typeNameHelperType.GetMethod("FindInterface")
      ?? throw new InvalidOperationException("TypeNameHelper.FindInterface(ITypeSymbol, string) not found.");
    return (INamedTypeSymbol?)method.Invoke(null, [type, interfaceFullName]);
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

  // ==================== GetImplementedInterfaces ====================

  [Test]
  public async Task GetImplementedInterfaces_TypeWithMultipleInterfaces_ReturnsEveryFullyQualifiedNameAsync() {
    // A generator that enumerates a type's contract surface (e.g. to report every interface it
    // satisfies for routing or diagnostics) must see all of them — missing one here means the
    // generator's output silently omits a contract the type genuinely implements.
    const string source = """
      namespace TestNamespace {
        public interface IFirst { }
        public interface ISecond { }
        public class Target : IFirst, ISecond { }
      }
      """;
    var symbol = _classSymbolFor(source, "Target");

    var interfaces = _getImplementedInterfaces(symbol);

    await Assert.That(interfaces.Contains("global::TestNamespace.IFirst")).IsTrue()
      .Because("every interface a type implements must be reported, not just the first one found");
    await Assert.That(interfaces.Contains("global::TestNamespace.ISecond")).IsTrue()
      .Because("every interface a type implements must be reported, not just the first one found");
  }

  [Test]
  public async Task GetImplementedInterfaces_TypeWithNoInterfaces_ReturnsEmptyArrayAsync() {
    // A plain type must report zero interfaces — a false positive here would make a caller believe
    // an ordinary class satisfies a routing contract it never declared.
    const string source = """
      namespace TestNamespace {
        public class Plain { }
      }
      """;
    var symbol = _classSymbolFor(source, "Plain");

    var interfaces = _getImplementedInterfaces(symbol);

    await Assert.That(interfaces).IsEmpty()
      .Because("a type with no declared interfaces must not be reported as implementing any");
  }

  // ==================== FindInterface ====================

  [Test]
  public async Task FindInterface_MatchingFullyQualifiedName_ReturnsTheInterfaceSymbolAsync() {
    // Callers use the returned symbol itself (e.g. to inspect its type arguments); returning null
    // for an interface the type genuinely implements would make a caller lose the ability to
    // inspect that contract at all.
    const string source = """
      namespace TestNamespace {
        public interface ITarget { }
        public class Target : ITarget { }
      }
      """;
    var symbol = _classSymbolFor(source, "Target");

    var found = _findInterface(symbol, "global::TestNamespace.ITarget");

    await Assert.That(found).IsNotNull();
    await Assert.That(found!.Name).IsEqualTo("ITarget")
      .Because("the matching interface's own symbol must be returned so a caller can inspect it further");
  }

  [Test]
  public async Task FindInterface_NoMatchingInterface_ReturnsNullAsync() {
    // A caller that treats a non-null result as "this contract is present" must get null when the
    // type does not implement the requested interface, or it would wrongly act as if it did.
    const string source = """
      namespace TestNamespace {
        public interface IOther { }
        public class Target : IOther { }
      }
      """;
    var symbol = _classSymbolFor(source, "Target");

    var found = _findInterface(symbol, "global::TestNamespace.INonExistent");

    await Assert.That(found).IsNull()
      .Because("a type that does not implement the requested interface must not produce a match");
  }
}
