// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The guard behind service-scoped registration (#2243): every service registration a generated module initializer
/// hands to the framework names the assembly it comes from, so a host can replay its own service's and leave the
/// others'.
/// </summary>
/// <remarks>
/// <para>
/// A host replays the registrations module initializers left in process-wide lists. One handed over without its
/// assembly (assigned to a slot, or passed to a registry that takes no assembly) is replayed into every host in the
/// process, which is the failure #2243 removes. This runs every generator in the generator assemblies over a source
/// that declares every kind of per-assembly item, finds every delegate over an <c>IServiceCollection</c> in a
/// generated <c>[ModuleInitializer]</c>, and requires it to be passed to one of the scoped sinks with the compilation's
/// own assembly name as the first argument.
/// </para>
/// <para>
/// The one other accepted sink is the DbContext registration, which is keyed by the DbContext type a host names
/// itself, so it cannot reach a host that did not ask for it.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators</code-under-test>
public class ServiceScopedRegistrationGuardTests {
  private const string ASSEMBLY = "GuardApp";

  private const string SOURCE = """
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    using Whizbang.Core.Lenses;
    using Whizbang.Core.Messaging;
    using Whizbang.Core.Perspectives;

    namespace GuardApp;

    [PinnedId("7b0e1d5c-2f43-4a7e-9d21-5b6c8e9f0a12")]
    [Topic("orders")]
    public record OrderPlaced : IEvent {
      [StreamId]
      public Guid OrderId { get; init; }
    }

    public record PlaceOrder : ICommand {
      [StreamId]
      public Guid OrderId { get; init; }
    }

    public record OrderModel {
      [StreamId]
      public Guid Id { get; init; }
    }

    public class OrderPerspective : IPerspectiveFor<OrderModel, OrderPlaced> {
      public OrderModel Apply(OrderModel current, OrderPlaced @event) => current;
    }

    public class PlaceOrderReceptor : IReceptor<PlaceOrder, OrderPlaced> {
      public ValueTask<OrderPlaced> HandleAsync(PlaceOrder message, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new OrderPlaced { OrderId = message.OrderId });
    }

    [FireAt(LifecycleStage.PostPerspectiveInline)]
    public class OrderAuditReceptor : IReceptor<OrderPlaced> {
      public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    public class OrderRawReceptor : IRawReceptor {
      public string TargetMessageTypeName => "GuardApp.OrderPlaced, GuardApp";
      public Task HandleAsync(JsonElement payload, CancellationToken ct) => Task.CompletedTask;
    }
    """;

  /// <summary>The sinks that take the registering assembly's name first.</summary>
  private static readonly HashSet<string> _scopedSinks = new(StringComparer.Ordinal) {
    "ServiceRegistrationCallbacks.RegisterLensServices",
    "ServiceRegistrationCallbacks.RegisterPerspectiveServices",
    "ServiceRegistrationCallbacks.RegisterDispatcher",
    "ServiceRegistrationCallbacks.RegisterRawReceptors",
    "ServiceRegistrationCallbacks.RegisterPinnedIdRegistry",
    "ServiceRegistrationCallbacks.RegisterMessageTypeCatalog",
    "PerspectiveRunnerCallbackRegistry.RegisterCallback",
    "ModelRegistrationRegistry.RegisterModels",
  };

  /// <summary>Sinks keyed by a type the host names itself, with the reason.</summary>
  private static readonly Dictionary<string, string> _keyedByTheHost = new(StringComparer.Ordinal) {
    ["DbContextRegistrationRegistry.Register"] = "keyed by the DbContext type; the driver runs only the one the host registers",
  };

  /// <summary>Every service registration in <paramref name="tree"/>'s module initializers, with how it was handed over.</summary>
  private static List<(string Sink, bool Ok)> _handOvers(SyntaxTree tree, SemanticModel? model) {
    var found = new List<(string Sink, bool Ok)>();
    var initializers = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
      .Where(m => m.AttributeLists.SelectMany(l => l.Attributes).Any(a => a.Name.ToString().EndsWith("ModuleInitializer", StringComparison.Ordinal)));
    foreach (var initializer in initializers) {
      foreach (var lambda in initializer.DescendantNodes().OfType<LambdaExpressionSyntax>().Where(l => _takesServices(l, model))) {
        found.Add(_classify(lambda));
      }
    }
    return found;
  }

  private static bool _takesServices(LambdaExpressionSyntax lambda, SemanticModel? model) {
    if (model?.GetSymbolInfo(lambda).Symbol is IMethodSymbol method) {
      return method.Parameters.Any(p => p.Type.Name == "IServiceCollection");
    }
    var parameters = lambda switch {
      SimpleLambdaExpressionSyntax simple => [simple.Parameter],
      ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.ToList(),
      _ => [],
    };
    return parameters.Any(p => p.Identifier.Text == "services");
  }

  private static (string Sink, bool Ok) _classify(LambdaExpressionSyntax lambda) {
    if (lambda.Parent is AssignmentExpressionSyntax assignment) {
      return ($"{assignment.Left} = ...", false);
    }
    if (lambda.Parent is not ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } arguments }) {
      return ($"a delegate handed over by {lambda.Parent?.Kind()}", false);
    }
    var callee = invocation.Expression is MemberAccessExpressionSyntax member
      ? $"{_lastName(member.Expression)}.{_withoutTypeArguments(member.Name)}"
      : invocation.Expression.ToString();
    if (_keyedByTheHost.ContainsKey(callee)) {
      return (callee, true);
    }
    var first = arguments.Arguments.Count > 1 ? arguments.Arguments[0].Expression : null;
    // The EF Core model registration names its assembly by its own generated type's assembly, which is the same name.
    var namesItsAssembly = first is LiteralExpressionSyntax literal
        && literal.IsKind(SyntaxKind.StringLiteralExpression)
        && literal.Token.ValueText == ASSEMBLY
      || first?.ToString() == "typeof(GeneratedModelRegistration).Assembly.GetName().Name!";
    return (callee, _scopedSinks.Contains(callee) && namesItsAssembly);
  }

  private static string _lastName(ExpressionSyntax expression) => expression switch {
    MemberAccessExpressionSyntax member => _withoutTypeArguments(member.Name),
    AliasQualifiedNameSyntax alias => _withoutTypeArguments(alias.Name),
    QualifiedNameSyntax qualified => _withoutTypeArguments(qualified.Right),
    SimpleNameSyntax simple => _withoutTypeArguments(simple),
    _ => expression.ToString(),
  };

  private static string _withoutTypeArguments(SimpleNameSyntax name) => name.Identifier.Text;

  /// <summary>The positive control: the detector takes the scoped form and rejects every unscoped one.</summary>
  [Test]
  public async Task TheGuard_TakesAnAssemblyNamedHandOver_AndRejectsEveryOtherAsync() {
    const string code = """
      internal static class Init {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Initialize() {
          global::Whizbang.Core.ServiceRegistrationCallbacks.RegisterDispatcher("GuardApp", services => { });
          global::Whizbang.Core.ServiceRegistrationCallbacks.Dispatcher = services => { };
          global::Whizbang.Core.Perspectives.PerspectiveRunnerCallbackRegistry.RegisterCallback(services => { });
          global::Whizbang.Core.ServiceRegistrationCallbacks.RegisterDispatcher("SomeoneElse", services => { });
          Whizbang.Data.EFCore.Postgres.DbContextRegistrationRegistry.Register<object>((services, name) => { });
          Elsewhere.Keep(static services => { });
          System.Action<object> notAServiceCallback = x => { };
        }
      }
      """;

    var handOvers = _handOvers(CSharpSyntaxTree.ParseText(code), model: null);

    await Assert.That(handOvers.Select(h => h.Ok)).IsEquivalentTo([true, false, false, false, true, false])
      .Because("found: " + string.Join(" | ", handOvers.Select(h => $"{h.Sink}={h.Ok}")));
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EveryServiceRegistrationAModuleInitializerHandsOver_NamesItsAssemblyAsync() {
    var generatorTypes = new[] {
      typeof(ReceptorDiscoveryGenerator).Assembly,
      typeof(Whizbang.Data.EFCore.Postgres.Generators.EFCoreServiceRegistrationGenerator).Assembly,
      typeof(Whizbang.Transports.HotChocolate.Generators.GraphQLLensTypeGenerator).Assembly,
      typeof(Whizbang.Transports.FastEndpoints.Generators.RestLensEndpointGenerator).Assembly,
    }.SelectMany(a => a.GetTypes())
      .Where(t => !t.IsAbstract && typeof(IIncrementalGenerator).IsAssignableFrom(t)
        && t.GetCustomAttributes(typeof(GeneratorAttribute), inherit: false).Length > 0)
      .ToList();
    var handOvers = new List<(string Sink, bool Ok)>();
    foreach (var type in generatorTypes) {
      var generator = (IIncrementalGenerator)Activator.CreateInstance(type)!;
      var (result, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(generator, SOURCE, ASSEMBLY);
      foreach (var tree in result.GeneratedTrees) {
        var outputTree = output.SyntaxTrees.FirstOrDefault(t => t.FilePath == tree.FilePath) ?? tree;
        var model = output.SyntaxTrees.Contains(outputTree) ? output.GetSemanticModel(outputTree) : null;
        handOvers.AddRange(_handOvers(outputTree, model));
      }
    }

    string[] expected = [
      "ServiceRegistrationCallbacks.RegisterDispatcher",
      "ServiceRegistrationCallbacks.RegisterRawReceptors",
      "ServiceRegistrationCallbacks.RegisterPinnedIdRegistry",
      "ServiceRegistrationCallbacks.RegisterMessageTypeCatalog",
      "PerspectiveRunnerCallbackRegistry.RegisterCallback",
    ];
    foreach (var sink in expected) {
      await Assert.That(handOvers.Select(h => h.Sink)).Contains(sink)
        .Because("precondition: the source declares every kind of per-assembly item, so each hand-over is generated");
    }
    var unscoped = handOvers.Where(h => !h.Ok).Select(h => h.Sink).Distinct().ToList();
    await Assert.That(unscoped).IsEmpty()
      .Because("a registration handed over without its assembly is replayed into every host in the process; pass the "
             + "assembly name first to a scoped sink instead (#2243). Found: " + string.Join(" | ", unscoped));
  }
}
