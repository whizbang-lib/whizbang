using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// Records what each registered type's constructor requires, so registration validation needs no
/// reflection.
/// </summary>
/// <remarks>
/// <para>
/// A dependency that is declared but never registered produces no error at run time: the container
/// supplies null, the dependent type runs in a degraded mode nobody chose, and the missing behavior
/// is indistinguishable from behavior that was never requested. Catching that requires knowing what
/// each registered type needs, and discovering it at run time would mean reading constructors
/// reflectively, which this framework does not permit.
/// </para>
/// <para>
/// So the answer is written down at compile time. Validation then compares type handles against the
/// registrations in a service collection, which is a scan over data.
/// </para>
/// <para>
/// Both inputs are derived from code that already has to exist: a registration call, and the
/// constructor of the type it registers. Nothing is annotated. A contributor who adds a constructor
/// parameter is covered without knowing this generator exists, which is the only property that
/// makes the guard survive contributors who have never heard of it. A manifest that had to be
/// remembered would fail in exactly the way the defect it guards against fails.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/registration-validation</docs>
/// <tests>Whizbang.Generators.Tests/ServiceRequirementsGeneratorTests.cs</tests>
[Generator]
public class ServiceRequirementsGenerator : IIncrementalGenerator {

  private static readonly string[] _registrationMethods = [
    "AddSingleton", "AddScoped", "AddTransient",
    "TryAddSingleton", "TryAddScoped", "TryAddTransient",
    "AddHostedService",
  ];

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var registrations = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => _isCandidateRegistration(node),
        transform: static (ctx, ct) => _extractRequirement(ctx, ct))
      .Where(static r => r is not null)
      .Select(static (r, _) => r!)
      .Collect();

    // The manifest is emitted into every compilation that runs this generator, so its namespace is
    // derived from the assembly. A fixed namespace would give two assemblies the same
    // fully-qualified type and collide wherever one can see the other's internals.
    var withAssembly = registrations.Combine(context.CompilationProvider
      .Select(static (c, _) => c.AssemblyName ?? "Whizbang.Core"));

    context.RegisterSourceOutput(withAssembly,
      static (spc, pair) => _emit(spc, pair.Left, pair.Right));
  }

  /// <summary>
  /// Cheap syntactic filter: a generic invocation whose name is a registration method.
  /// </summary>
  private static bool _isCandidateRegistration(SyntaxNode node) {
    if (node is not InvocationExpressionSyntax invocation) {
      return false;
    }
    if (invocation.Expression is not MemberAccessExpressionSyntax member) {
      return false;
    }
    if (member.Name is not GenericNameSyntax generic) {
      return false;
    }

    var name = generic.Identifier.ValueText;
    for (var i = 0; i < _registrationMethods.Length; i++) {
      if (string.Equals(name, _registrationMethods[i], System.StringComparison.Ordinal)) {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Resolves the implementation type of a registration and the service types it requires.
  /// </summary>
  private static RequirementInfo? _extractRequirement(GeneratorSyntaxContext ctx, CancellationToken ct) {
    ct.ThrowIfCancellationRequested();

    var invocation = (InvocationExpressionSyntax)ctx.Node;
    var member = (MemberAccessExpressionSyntax)invocation.Expression;
    var generic = (GenericNameSyntax)member.Name;
    var args = generic.TypeArgumentList.Arguments;
    if (args.Count is 0 or > 2) {
      return null;
    }

    // One type argument registers that type as its own implementation; two register the second as
    // the implementation of the first. Either way the LAST argument is what actually gets built,
    // and only a constructed type has a constructor to inspect.
    var implementationSyntax = args[args.Count - 1];
    if (ctx.SemanticModel.GetSymbolInfo(implementationSyntax, ct).Symbol is not INamedTypeSymbol implementation) {
      return null;
    }
    if (implementation.IsAbstract || implementation.TypeKind == TypeKind.Interface) {
      return null;
    }
    // The manifest is a source file in the same assembly, so it can only name types it can see.
    // Private and protected nested types (test fixtures, mostly) would emit code that does not
    // compile, turning a guard into a build break for everyone.
    if (!_isReferenceable(implementation)) {
      return null;
    }

    var ctor = implementation.Constructors
      .Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsStatic)
      .OrderByDescending(c => c.Parameters.Length)
      .FirstOrDefault();
    if (ctor is null || ctor.Parameters.Length == 0) {
      return null;
    }

    var dependencies = new List<string>();
    foreach (var p in ctor.Parameters) {
      // Only interfaces are container-resolved services here. Demanding a registration for a
      // string, an int, or a token would fail every composition, and a guard that always fails is
      // a guard that gets switched off.
      if (p.Type.TypeKind != TypeKind.Interface) {
        continue;
      }
      if (p.Type is not INamedTypeSymbol dependency || !_isReferenceable(dependency)) {
        continue;
      }
      dependencies.Add(p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    if (dependencies.Count == 0) {
      return null;
    }

    return new RequirementInfo(
      implementation.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
      dependencies);
  }

  /// <summary>
  /// True when generated source in the same assembly can name this type.
  /// </summary>
  private static bool _isReferenceable(INamedTypeSymbol type) {
    for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType) {
      if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)) {
        return false;
      }
    }
    return true;
  }

  private static void _emit(
      SourceProductionContext spc, ImmutableArray<RequirementInfo> items, string assemblyName) {
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("#nullable enable");
    sb.Append("namespace ").Append(assemblyName).AppendLine(".DependencyInjection;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Constructor dependencies of every registered type in this compilation.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("internal static class WhizbangServiceRequirements {");
    sb.AppendLine();
    sb.AppendLine("  /// <summary>Every registered implementation type and what it requires.</summary>");
    sb.AppendLine("  internal static readonly global::Whizbang.Core.DependencyInjection.ServiceRequirement[] All = [");

    // Two registrations of the same type contribute the same requirement; emitting it twice would
    // report the same gap twice and overstate how many things are broken.
    var seen = new HashSet<string>(System.StringComparer.Ordinal);
    foreach (var item in items.OrderBy(i => i.ImplementationType, System.StringComparer.Ordinal)) {
      if (!seen.Add(item.ImplementationType)) {
        continue;
      }
      sb.Append("    new global::Whizbang.Core.DependencyInjection.ServiceRequirement(typeof(").Append(item.ImplementationType).AppendLine("), [");
      foreach (var dep in item.Dependencies) {
        sb.Append("      typeof(").Append(dep).AppendLine("),");
      }
      sb.AppendLine("    ]),");
    }

    sb.AppendLine("  ];");
    sb.AppendLine("}");

    spc.AddSource("WhizbangServiceRequirements.g.cs", sb.ToString());
  }

  /// <summary>One registered implementation and the service types its constructor requires.</summary>
  private sealed record RequirementInfo(string ImplementationType, List<string> Dependencies);
}
