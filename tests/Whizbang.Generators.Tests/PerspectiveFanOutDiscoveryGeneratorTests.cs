// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <c>PerspectiveFanOutDiscoveryGenerator</c> (#2618): one registry entry per (model, foreign event) a
/// perspective fans out, with the key column read from its <c>Targets</c> at build time, registered as the assembly's
/// contribution to the fan-out registry the host serves.
/// </summary>
/// <tests>Whizbang.Generators/PerspectiveFanOutDiscoveryGenerator.cs</tests>
[Category("Generators")]
[Category("FanOut")]
public class PerspectiveFanOutDiscoveryGeneratorTests {
  private const string FILE = "PerspectiveFanOutRegistry.g.cs";

  private const string PREAMBLE = """
      using System;
      using System.Linq.Expressions;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public sealed class OrderSummary {
        [PhysicalField]
        [Indexed]
        public Guid CustomerId { get; init; }

        [PhysicalField(ColumnName = "region")]
        [Indexed]
        public Guid RegionId { get; init; }

        public string CustomerName { get; init; } = "";
        public bool CustomerDeleted { get; init; }
      }

      public sealed record CustomerRenamed(string Name) : IEvent;
      public sealed record CustomerDeleted : IEvent;
      public sealed record RegionRenamed(string Name) : IEvent;

      public sealed record Spec<T>(Expression<Action<ICollectiveSetters<T>>> Setters) : ICollectiveSpec<T> where T : class;

      """;

  private const string ONE_KEY_TWO_EVENTS = PREAMBLE + """
      public class OrderSummaryProjection
        : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed>,
          IPerspectiveFanOutFor<OrderSummary, CustomerDeleted> {
        public static FanOutKey<OrderSummary> Targets => FanOut.By<OrderSummary>(o => o.CustomerId);

        public ICollectiveSpec<OrderSummary> Apply(CustomerRenamed e) =>
          new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, e.Name));

        public ICollectiveSpec<OrderSummary> Apply(CustomerDeleted e) =>
          new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerDeleted, true));
      }
      """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_OneEntryPerModelAndForeignEvent_WithTheKeyColumnAsync() {
    var (result, _) = GeneratorTestHelper.RunGeneratorWithFullReferences(
        new PerspectiveFanOutDiscoveryGenerator(), ONE_KEY_TWO_EVENTS, "Orders.Service");
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE);

    await Assert.That(_count(code, "new global::Whizbang.Core.Perspectives.PerspectiveFanOutEntry(")).IsEqualTo(2)
      .Because("each (model, foreign event) the perspective implements is one fan-out");
    await Assert.That(code).Contains("EventType: typeof(global::TestApp.CustomerRenamed)");
    await Assert.That(code).Contains("EventType: typeof(global::TestApp.CustomerDeleted)");
    await Assert.That(_count(code, "KeyColumn: \"customer_id\"")).IsEqualTo(2)
      .Because("one implicit Targets serves both interfaces, and the column is the snake_case of the key property");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_TheEntry_IsTheSnapshotOfTheDeclarationAsync() {
    var (result, _) = GeneratorTestHelper.RunGeneratorWithFullReferences(
        new PerspectiveFanOutDiscoveryGenerator(), ONE_KEY_TWO_EVENTS, "Orders.Service");
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE).Replace("\r\n", "\n");

    const string expected = """
          new global::Whizbang.Core.Perspectives.PerspectiveFanOutEntry(
            PerspectiveType: typeof(global::TestApp.OrderSummaryProjection),
            ModelType: typeof(global::TestApp.OrderSummary),
            EventType: typeof(global::TestApp.CustomerRenamed),
            KeyProperty: "CustomerId",
            KeyColumn: "customer_id",
            KeySelector: (global::System.Linq.Expressions.Expression<global::System.Func<global::TestApp.OrderSummary, object?>>)(row => row.CustomerId),
            Invoker: static (perspective, evt) => ((global::Whizbang.Core.Perspectives.IPerspectiveFanOutFor<global::TestApp.OrderSummary, global::TestApp.CustomerRenamed>)perspective).Apply((global::TestApp.CustomerRenamed)evt)
          ),
      """;
    await Assert.That(code).Contains(expected.Replace("\r\n", "\n"));
    await Assert.That(code).Contains("namespace Orders.Service.Generated");
    await Assert.That(code).Contains("public sealed class GeneratedPerspectiveFanOutRegistry : global::Whizbang.Core.Perspectives.IPerspectiveFanOutRegistry");
    await Assert.That(code).Contains("global::Whizbang.Core.ServiceRegistrationCallbacks.RegisterPerspectiveFanOutRegistry(\"Orders.Service\",")
      .Because("the module initializer hands the registry to AddWhizbang() through the slot named by the assembly");
    await Assert.That(code).Contains("AddPerspectiveFanOutRegistryContribution<GeneratedPerspectiveFanOutRegistry>(services)");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ExplicitTargetsPerInterface_GiveEachEventItsOwnKeyAsync() {
    const string source = PREAMBLE + """
        public class OrderSummaryProjection
          : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed>,
            IPerspectiveFanOutFor<OrderSummary, RegionRenamed> {
          static FanOutKey<OrderSummary> IPerspectiveFanOutFor<OrderSummary, CustomerRenamed>.Targets =>
            FanOut.By<OrderSummary>(o => o.CustomerId);

          static FanOutKey<OrderSummary> IPerspectiveFanOutFor<OrderSummary, RegionRenamed>.Targets {
            get { return FanOut.By<OrderSummary>(o => o.RegionId); }
          }

          public ICollectiveSpec<OrderSummary> Apply(CustomerRenamed e) =>
            new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, e.Name));

          ICollectiveSpec<OrderSummary> IPerspectiveFanOutFor<OrderSummary, RegionRenamed>.Apply(RegionRenamed e) =>
            new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, e.Name));
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveFanOutDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE).Replace("\r\n", "\n");

    await Assert.That(code).Contains("EventType: typeof(global::TestApp.CustomerRenamed),\n      KeyProperty: \"CustomerId\",\n      KeyColumn: \"customer_id\",");
    await Assert.That(code).Contains("EventType: typeof(global::TestApp.RegionRenamed),\n      KeyProperty: \"RegionId\",\n      KeyColumn: \"region\",")
      .Because("an explicit Targets is read per interface, and a declared ColumnName is the column");
    await Assert.That(code).Contains("Func<global::TestApp.OrderSummary, object?>>)(row => row.RegionId)")
      .Because("#2620: the key selector the live apply matches rows by is the key each interface declares");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_TheGeneratedRegistry_CompilesAsync() {
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<PerspectiveFanOutDiscoveryGenerator>(ONE_KEY_TWO_EVENTS);

    await Assert.That(errors).IsEmpty()
      .Because("the invoker casts to the fan-out interface and calls Apply, which must bind for an implicit and an explicit Apply");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithNoFanOut_EmitsNothingAsync() {
    const string source = PREAMBLE + """
        public sealed record OrderPlaced : IEvent;

        public class OrderSummaryProjection : IPerspectiveFor<OrderSummary, OrderPlaced> {
          public OrderSummary Apply(OrderSummary current, OrderPlaced @event) => current;
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveFanOutDiscoveryGenerator>(source);

    await Assert.That(GeneratorTestHelper.GetAllGeneratedSources(result)).IsEmpty()
      .Because("a perspective with no fan-out emits exactly what it emitted before fan-outs existed");
  }

  [Test]
  [RequiresAssemblyFiles]
  [Arguments("FanOut.By<OrderSummary>(o => o.CustomerName)", "a key that is not a physical field")]
  [Arguments("FanOut.By<OrderSummary>(o => Guid.Empty)", "a selector that does not name a member")]
  [Arguments("FanOut.By<OrderSummary>(o => { return o.CustomerId; })", "a block lambda, which an expression tree cannot hold")]
  [Arguments("Nope(o => o.CustomerId)", "a call that does not bind")]
  public async Task Generator_ADeclarationTheAnalyzerRefuses_IsLeftOutAsync(string targets, string why) {
    var source = PREAMBLE + $$"""
        public class OrderSummaryProjection : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
          public static FanOutKey<OrderSummary> Targets => {{targets}};

          public ICollectiveSpec<OrderSummary> Apply(CustomerRenamed e) =>
            new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, e.Name));
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveFanOutDiscoveryGenerator>(source);

    await Assert.That(GeneratorTestHelper.GetAllGeneratedSources(result)).IsEmpty()
      .Because($"{why} is reported by the analyzer and never registered");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AnAbstractPerspective_IsLeftOutAsync() {
    const string source = PREAMBLE + """
        public abstract class OrderSummaryProjection : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
          public static FanOutKey<OrderSummary> Targets => FanOut.By<OrderSummary>(o => o.CustomerId);
          public abstract ICollectiveSpec<OrderSummary> Apply(CustomerRenamed e);
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveFanOutDiscoveryGenerator>(source);

    await Assert.That(GeneratorTestHelper.GetAllGeneratedSources(result)).IsEmpty()
      .Because("an abstract type is never resolved from the container, so its concrete subclass is the fan-out");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_APartialPerspective_WithTargetsInAnotherFile_IsDiscoveredOnceAsync() {
    const string first = PREAMBLE + """
        public partial class OrderSummaryProjection : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
          public ICollectiveSpec<OrderSummary> Apply(CustomerRenamed e) =>
            new Spec<OrderSummary>(s => s.SetProperty(o => o.CustomerName, e.Name));
        }
        """;
    const string second = """
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public partial class OrderSummaryProjection : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
          public static FanOutKey<OrderSummary> Targets { get; } = FanOut.By<OrderSummary>(o => o.CustomerId);
        }
        """;
    var compilation = GeneratorTestHelper.CreateCompilationWithFullReferences("TestAssembly", first, second);

    var result = GeneratorTestHelper.RunGenerator<PerspectiveFanOutDiscoveryGenerator>(compilation, []);
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE);

    await Assert.That(_count(code, "new global::Whizbang.Core.Perspectives.PerspectiveFanOutEntry(")).IsEqualTo(1)
      .Because("both parts list the interface, and the fan-out is one; its Targets is bound in the file that declares it");
    await Assert.That(code).Contains("KeyColumn: \"customer_id\"");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_TargetsDeclaredInAReferencedAssembly_IsLeftOutAsync() {
    const string library = PREAMBLE + """
        public class FanOutBase {
          public static FanOutKey<OrderSummary> Targets => FanOut.By<OrderSummary>(o => o.CustomerId);
        }
        """;
    await using var image = new MemoryStream();
    var emitted = GeneratorTestHelper.CreateCompilationWithFullReferences("Library", library).Emit(image);
    await Assert.That(emitted.Success).IsTrue();
    const string consumer = """
        using Whizbang.Core.Perspectives;
        using TestApp;

        namespace Consumer;

        public class OrderSummaryProjection : FanOutBase, IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
          public ICollectiveSpec<OrderSummary> Apply(CustomerRenamed e) => null!;
        }
        """;
    var compilation = GeneratorTestHelper.CreateCompilationWithFullReferences("Consumer", consumer)
      .AddReferences(MetadataReference.CreateFromImage(image.ToArray()));

    var result = GeneratorTestHelper.RunGenerator<PerspectiveFanOutDiscoveryGenerator>(compilation, []);

    await Assert.That(GeneratorTestHelper.GetAllGeneratedSources(result)).IsEmpty()
      .Because("a Targets with no source cannot be read here; the library's own build checked it");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_APerspectiveThatDoesNotImplementTargets_IsLeftOutAsync() {
    const string source = PREAMBLE + """
        public class OrderSummaryProjection : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
          public ICollectiveSpec<OrderSummary> Apply(CustomerRenamed e) => null!;
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveFanOutDiscoveryGenerator>(source);

    await Assert.That(GeneratorTestHelper.GetAllGeneratedSources(result)).IsEmpty()
      .Because("the compiler reports the missing member, and there is no key to register");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AnUnnamedAssembly_NamesItsContributionUnknownAssemblyAsync() {
    var result = GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(new PerspectiveFanOutDiscoveryGenerator(), ONE_KEY_TWO_EVENTS);
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE);

    await Assert.That(code).Contains("namespace UnknownAssembly.Generated");
  }

  [Test]
  [RequiresAssemblyFiles]
  [Arguments("Whizbang.Core.Perspectives.IPerspectiveFanOutFor<OrderSummary, CustomerRenamed>")]
  [Arguments("global::Whizbang.Core.Perspectives.IPerspectiveFanOutFor<OrderSummary, CustomerRenamed>")]
  public async Task Generator_AQualifiedInterfaceName_IsDiscoveredAsync(string named) {
    var source = PREAMBLE + $$"""
        public class OrderSummaryProjection : {{named}} {
          public static FanOutKey<OrderSummary> Targets => FanOut.By<OrderSummary>(o => o.CustomerId);
          public ICollectiveSpec<OrderSummary> Apply(CustomerRenamed e) => null!;
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveFanOutDiscoveryGenerator>(source);

    await Assert.That(GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE)).Contains("KeyColumn: \"customer_id\"");
  }

  [Test]
  public async Task IsFanOutCandidate_AdmitsOnlyATypeThatListsTheInterfaceAsync() {
    var root = await CSharpSyntaxTree.ParseText("""
        class A : IPerspectiveFanOutFor<M, E> { }
        class B : IPerspectiveFor<M, E> { }
        class C : IPerspectiveFanOutFor<M> { }
        class D { }
        class E : global::IPerspectiveFanOutFor<M, E> { }
        """).GetRootAsync();
    var admitted = root.DescendantNodes().Where(PerspectiveFanOutDiscoveryGenerator.IsFanOutCandidate)
      .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>().Select(t => t.Identifier.ValueText).ToArray();

    await Assert.That(admitted).IsEquivalentTo(["A", "E"]);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AKeyOfAStrongIdTheIdGeneratorEmitsFromTheProperty_IsDiscoveredAsync() {
    const string source = PREAMBLE + """
        public sealed class Shipment {
          [PhysicalField]
          [Indexed]
          [WhizbangId]
          public CarrierId CarrierId { get; init; }
        }

        public sealed record CarrierRenamed : IEvent;

        public class ShipmentProjection : IPerspectiveFanOutFor<Shipment, CarrierRenamed> {
          public static FanOutKey<Shipment> Targets => FanOut.By<Shipment>(s => s.CarrierId);
          public ICollectiveSpec<Shipment> Apply(CarrierRenamed e) => null!;
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveFanOutDiscoveryGenerator>(source);

    await Assert.That(GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE)).Contains("KeyColumn: \"carrier_id\"")
      .Because("the id type is emitted by a sibling generator from the property's [WhizbangId], which this generator cannot see");
  }

  private static int _count(string code, string text) {
    var count = 0;
    for (var at = code.IndexOf(text, StringComparison.Ordinal); at >= 0; at = code.IndexOf(text, at + text.Length, StringComparison.Ordinal)) {
      count++;
    }
    return count;
  }
}
