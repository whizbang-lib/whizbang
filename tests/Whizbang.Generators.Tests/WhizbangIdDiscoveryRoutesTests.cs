using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The three routes <see cref="WhizbangIdGenerator"/> discovers an id through, and the options
/// each of them has to honor identically.
/// </summary>
/// <remarks>
/// An id can be declared as its own partial struct, or inferred from a property or a constructor
/// parameter that uses it. All three end at the same generated value object, so an option honored
/// on one route and dropped on another produces a type in the wrong namespace — which fails the
/// consumer's build, in generated code, with no indication that the attribute argument was the
/// cause.
///
/// <para>
/// The routes are also selected by a syntax predicate that matches <em>any</em> attribute, so
/// every struct, property and parameter carrying an unrelated attribute reaches the extractor and
/// has to be declined. That is the common case at rest, and it had no test.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/WhizbangIdGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class WhizbangIdDiscoveryRoutesTests {

  private static IEnumerable<(string FileName, string Source)> _generated(string source)
    => GeneratorTestHelper.GetAllGeneratedSources(
      GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source));

  private static string? _idFile(string source, string typeName)
    => _generated(source).FirstOrDefault(g => g.FileName == $"{typeName}.g.cs").Source;

  // ============================================================
  // An unrelated attribute must be declined on every route
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StructWithAnUnrelatedAttribute_IsNotTreatedAsAnIdAsync() {
    // The predicate matches any attribute list, so an ordinary annotated struct reaches the
    // extractor. Generating a value object for it would replace the consumer's own type.
    var files = _generated("""
      using System;

      namespace MyApp.Domain;

      [Obsolete("use something else")]
      public readonly partial struct NotAnId;
      """).ToList();

    await Assert.That(files.Any(f => f.FileName == "NotAnId.g.cs")).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PropertyWithAnUnrelatedAttribute_IsNotTreatedAsAnIdAsync() {
    var files = _generated("""
      using System;

      namespace MyApp.Domain;

      public class Product {
        [Obsolete("use Sku")]
        public string LegacyCode { get; set; } = "";
      }
      """).ToList();

    await Assert.That(files.Any(f => f.FileName == "String.g.cs")).IsFalse()
      .Because("generating a value object named after the property's type would collide with the BCL");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ParameterWithAnUnrelatedAttribute_IsNotTreatedAsAnIdAsync() {
    var files = _generated("""
      using System.Runtime.CompilerServices;

      namespace MyApp.Domain;

      public class Logger {
        public void Log(string message, [CallerMemberName] string caller = "") { }
      }
      """).ToList();

    await Assert.That(files.Any(f => f.FileName == "String.g.cs")).IsFalse();
  }

  // ============================================================
  // The namespace constructor argument, on every route
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TypeRoute_HonorsTheNamespaceConstructorArgumentAsync() {
    var generated = _idFile("""
      using Whizbang.Core;

      namespace MyApp.Domain;

      [WhizbangId("MyApp.Ids")]
      public readonly partial struct OrderId;
      """, "OrderId");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("namespace MyApp.Ids");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PropertyRoute_HonorsTheNamespaceConstructorArgumentAsync() {
    // The same argument on a different route. Honoring it on one and not the other puts the
    // generated type where nothing references it.
    var generated = _idFile("""
      using Whizbang.Core;

      namespace MyApp.Domain;

      public class Order {
        [WhizbangId("MyApp.Ids")]
        public OrderId Id { get; set; }
      }
      """, "OrderId");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("namespace MyApp.Ids");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ParameterRoute_HonorsTheNamespaceConstructorArgumentAsync() {
    var generated = _idFile("""
      using Whizbang.Core;

      namespace MyApp.Domain;

      public record Order([WhizbangId("MyApp.Ids")] OrderId Id);
      """, "OrderId");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("namespace MyApp.Ids");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ParameterRoute_HonorsTheNamespaceNamedArgumentAsync() {
    var generated = _idFile("""
      using Whizbang.Core;

      namespace MyApp.Domain;

      public record Order([WhizbangId(Namespace = "MyApp.Ids")] OrderId Id);
      """, "OrderId");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("namespace MyApp.Ids");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnEmptyNamespaceArgument_FallsBackToTheDeclaringNamespaceAsync() {
    // An empty string is not a namespace. Taking it literally would emit `namespace ;`, which
    // does not parse — a generated file the consumer cannot fix.
    var generated = _idFile("""
      using Whizbang.Core;

      namespace MyApp.Domain;

      [WhizbangId("")]
      public readonly partial struct OrderId;
      """, "OrderId");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("namespace MyApp.Domain");
  }

  // ============================================================
  // Every route reaches the same generated shape
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AllThreeRoutes_ProduceCompilableOutputAsync() {
    // The routes converge on one emitter, so a route that assembles its info differently shows
    // up here rather than as a subtly different type.
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<WhizbangIdGenerator>("""
      using Whizbang.Core;

      namespace MyApp.Domain;

      [WhizbangId]
      public readonly partial struct DeclaredId;

      public class Holder {
        [WhizbangId]
        public PropertyId Id { get; set; }
      }

      public record Carrier([WhizbangId] ParameterId Id);
      """);

    await Assert.That(errors).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheSameIdReachedByTwoRoutes_IsGeneratedOnceAsync() {
    // A record parameter and a property of the same id type is ordinary. Emitting the value
    // object twice is a duplicate type definition.
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<WhizbangIdGenerator>("""
      using Whizbang.Core;

      namespace MyApp.Domain;

      public class Holder {
        [WhizbangId]
        public OrderId Id { get; set; }
      }

      public record Carrier([WhizbangId] OrderId Id);
      """);

    await Assert.That(errors.Any(d => d.Id is "CS0101" or "CS0111")).IsFalse()
      .Because("the same id discovered twice must still be one generated type");
  }
}
