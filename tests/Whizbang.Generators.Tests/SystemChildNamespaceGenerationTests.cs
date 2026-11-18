// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Custom;
using Whizbang.Data.EFCore.Postgres.Generators;
using Whizbang.Transports.FastEndpoints.Generators;
using Whizbang.Transports.HotChocolate.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Generated code names framework types through <c>global::</c> (#2400). A bare <c>System.X</c> binds to the nearest
/// namespace named <c>System</c>, so in a project whose namespaces have a <c>System</c> child (an assembly named
/// <c>Whizbang.Core.Perspectives.Tests</c> beside a namespace <c>Whizbang.Core.Perspectives.System</c>, or a consumer's
/// own <c>Company.System</c>), the generated code did not compile.
/// </summary>
/// <remarks>
/// Two guards. Every generator in the core package runs over a consumer whose namespaces have <c>System</c> children,
/// and the result must compile, beside a control without them that proves the consumer itself compiles. And the
/// generated source of every generator package this project can run is read for a <c>System</c> reference without
/// <c>global::</c>, which fails whatever namespaces the consumer has. A using directive at the top of a file binds from
/// the global namespace, and a namespace declaration's name is absolute, so neither is a finding.
/// </remarks>
/// <tests>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</tests>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</tests>
[Category("SourceGenerators")]
public class SystemChildNamespaceGenerationTests {
  // The implicit usings an SDK project gets, which some generated code relies on.
  private const string IMPLICIT_USINGS = """
    global using System;
    global using System.Collections.Generic;
    global using System.Linq;
    global using System.Threading;
    global using System.Threading.Tasks;

    """;

  // Namespaces named System beside the consumer's and the generated code's: a bare System.X binds to one of these.
  private const string SYSTEM_CHILDREN = """

    namespace Consumer.System { internal static class Shadow { } }
    namespace Consumer.App.System { internal static class Shadow { } }
    namespace Consumer.App.Shop.System { internal static class Shadow { } }
    """;

  private const string CONSUMER = IMPLICIT_USINGS + """
    namespace Consumer.App.Shop {
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      using Whizbang.Core.Perspectives;

      [WhizbangId]
      public readonly partial struct OrderId;

      public enum OrderStatus { Open, Closed }

      [PinnedId("11111111-2222-3333-4444-555555555555")]
      public record OrderPlaced : IEvent {
        [StreamId] public Guid OrderId { get; init; }
        [PopulateTimestamp(TimestampKind.SentAt)] public DateTimeOffset? SentAt { get; init; }
        [PopulateFromContext(ContextKind.UserId)] public string? UserId { get; init; }
        public decimal Total { get; init; }
        public OrderStatus Status { get; init; }
      }

      [TopicFilter("orders.create")]
      public record PlaceOrder : ICommand {
        [StreamId][GenerateStreamId] public Guid OrderId { get; init; }
        public decimal Total { get; init; }
      }

      public class PlaceOrderReceptor : IReceptor<PlaceOrder, OrderPlaced> {
        public ValueTask<OrderPlaced> HandleAsync(PlaceOrder message, CancellationToken cancellationToken = default) =>
          ValueTask.FromResult(new OrderPlaced { OrderId = message.OrderId, Total = message.Total });
      }

      public class OrderPlacedReceptor : IReceptor<OrderPlaced> {
        public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
      }

      public record OrderView {
        [StreamId] public Guid OrderId { get; init; }
        [PhysicalField] public OrderStatus Status { get; init; }
        [PhysicalField] public decimal Total { get; init; }
        [VectorField(3)] public float[]? Embedding { get; init; }
        public List<string> Notes { get; init; } = [];
        public Dictionary<string, int> Counts { get; init; } = [];
      }

      public class OrderPerspective : IPerspectiveFor<OrderView, OrderPlaced> {
        public OrderView Apply(OrderView currentData, OrderPlaced @event) =>
          currentData with { OrderId = @event.OrderId, Total = @event.Total, Status = @event.Status };
      }
    }
    """;

  /// <summary>Every generator in the core package, run together as a consumer's build runs them.</summary>
  [RequiresUnreferencedCode("Finds the generators by reflection over the generator assembly.")]
  private static IIncrementalGenerator[] _coreGenerators() =>
    [.. typeof(ReceptorDiscoveryGenerator).Assembly.GetTypes()
      .Where(type => !type.IsAbstract && type.GetCustomAttribute<GeneratorAttribute>() is not null
        && typeof(IIncrementalGenerator).IsAssignableFrom(type))
      .OrderBy(type => type.Name, StringComparer.Ordinal)
      .Select(type => (IIncrementalGenerator)Activator.CreateInstance(type)!)];

  /// <summary>The consumer compiles with its generated code, so a failure below comes from the System children.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the generators by reflection over the generator assembly.")]
  public async Task WithoutSystemChildren_TheGeneratedCodeCompilesAsync() {
    var (result, output) = GeneratorTestHelper.RunGeneratorsWithFullReferences(_coreGenerators(), CONSUMER, "Consumer.App");

    await Assert.That(result.GeneratedTrees.Length).IsGreaterThan(10).Because("the consumer must make the generators emit");
    await Assert.That(_errorsByFile(output)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the generators by reflection over the generator assembly.")]
  public async Task WithSystemChildren_TheGeneratedCodeStillCompilesAsync() {
    var (result, output) = GeneratorTestHelper.RunGeneratorsWithFullReferences(
      _coreGenerators(), CONSUMER + SYSTEM_CHILDREN, "Consumer.App");

    await Assert.That(result.GeneratedTrees.Length).IsGreaterThan(10);
    await Assert.That(_errorsByFile(output)).IsEmpty()
      .Because("a System.X the generators write must not bind to the consumer's own System namespace");
  }

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the generators by reflection over the generator assembly.")]
  public async Task CoreGenerators_NameEverySystemTypeThroughGlobalAsync() {
    var (result, _) = GeneratorTestHelper.RunGeneratorsWithFullReferences(_coreGenerators(), CONSUMER, "Consumer.App");

    await Assert.That(UnqualifiedSystemReferences(result.GeneratedTrees)).IsEmpty();
  }

  private const string EFCORE_CONSUMER = CONSUMER + """

    namespace Consumer.App.Data {
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      [WhizbangDbContext]
      public partial class ShopDbContext : DbContext {
        public ShopDbContext(DbContextOptions<ShopDbContext> options) : base(options) { }
      }
    }
    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task EFCoreGenerators_NameEverySystemTypeThroughGlobalAsync() {
    IIncrementalGenerator[] generators = [
      new EFCoreServiceRegistrationGenerator(), new EFCorePerspectiveConfigurationGenerator(),
      new EFCorePerspectiveAssociationGenerator(), new PerspectivePersistenceJsonContextGenerator(),
    ];
    var (result, _) = GeneratorTestHelper.RunGeneratorsWithFullReferences(generators, EFCORE_CONSUMER, "Consumer.App", [
      SharedMetadataReferences.FromFile(typeof(DbContext).Assembly.Location),
      SharedMetadataReferences.FromFile(typeof(WhizbangDbContextAttribute).Assembly.Location),
    ]);

    await Assert.That(result.GeneratedTrees.Length).IsGreaterThan(2).Because("the DbContext must make the generators emit");
    await Assert.That(UnqualifiedSystemReferences(result.GeneratedTrees)).IsEmpty();
  }

  private const string LENS_CONSUMER = """
    namespace Consumer.App.Shop {
      using Whizbang.Core.Lenses;

      public class Order {
        public System.Guid Id { get; set; }
        public string Title { get; set; } = "";
        public System.DateTimeOffset PlacedAt { get; set; }
      }

      [Whizbang.Transports.FastEndpoints.RestLens(Route = "/api/orders")]
      [Whizbang.Transports.HotChocolate.GraphQLLens(QueryName = "orders")]
      public interface IOrderLens : ILensQuery<Order> { }
    }
    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task TransportGenerators_NameEverySystemTypeThroughGlobalAsync() {
    IIncrementalGenerator[] generators = [
      new RestLensEndpointGenerator(), new RestMutationEndpointGenerator(),
      new GraphQLLensTypeGenerator(), new GraphQLMutationTypeGenerator(),
    ];
    var (result, _) = GeneratorTestHelper.RunGeneratorsWithFullReferences(generators, LENS_CONSUMER, "Consumer.App", [
      SharedMetadataReferences.FromFile(typeof(Whizbang.Transports.FastEndpoints.RestLensAttribute).Assembly.Location),
      SharedMetadataReferences.FromFile(typeof(Whizbang.Transports.HotChocolate.GraphQLLensAttribute).Assembly.Location),
    ]);

    await Assert.That(result.GeneratedTrees.Length).IsGreaterThan(1).Because("the lens must make both transports emit");
    await Assert.That(UnqualifiedSystemReferences(result.GeneratedTrees)).IsEmpty();
  }

  /// <summary>The scan finds a bare System name, and passes the forms that bind from the global namespace.</summary>
  [Test]
  public async Task TheScan_FindsABareSystemName_AndPassesGlobalUsingsAndNamespaceNamesAsync() {
    var tree = CSharpSyntaxTree.ParseText("""
      using System.Text;
      namespace System.Runtime.CompilerServices { }
      namespace Consumer.Generated {
        internal static class Sample {
          public static global::System.Guid A() => global::System.Guid.Empty;
          public static System.Guid B() => System.Guid.Empty;
        }
      }
      """, path: "Sample.g.cs");

    var found = UnqualifiedSystemReferences([tree]);

    await Assert.That(found).Count().IsEqualTo(2);
    await Assert.That(found.All(f => f.StartsWith("Sample.g.cs:6:", StringComparison.Ordinal))).IsTrue();
  }

  /// <summary>
  /// Each reference in <paramref name="trees"/> whose leftmost name is <c>System</c> without <c>global::</c>, as
  /// <c>file:line: text</c>, outside a using directive at the top of a file and a namespace declaration's own name.
  /// </summary>
  internal static List<string> UnqualifiedSystemReferences(IEnumerable<SyntaxTree> trees) =>
    [.. trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
      .Where(id => id.Identifier.ValueText == "System"
        && ((id.Parent is QualifiedNameSyntax q && q.Left == id) || (id.Parent is MemberAccessExpressionSyntax m && m.Expression == id))
        && !id.Ancestors().Any(a => a is UsingDirectiveSyntax { Parent: CompilationUnitSyntax })
        && !id.Ancestors().Any(a => a is BaseNamespaceDeclarationSyntax n && n.Name.Span.Contains(id.Span)))
      .Select(id => $"{Path.GetFileName(tree.FilePath)}:{id.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {id.Parent}"))];

  // One line per generated file with errors, naming its first error, so a failure lists every generator at fault.
  private static List<string> _errorsByFile(Compilation output) =>
    [.. output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
      .GroupBy(static e => Path.GetFileName(e.Location.SourceTree?.FilePath ?? "(no file)"))
      .Select(static g => $"{g.Key}: {g.Count()} error(s), first {g.First().Id} {g.First().GetMessage(System.Globalization.CultureInfo.InvariantCulture)}")
      .Order(StringComparer.Ordinal)];
}
