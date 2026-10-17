// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

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
/// <tests>tests/Whizbang.Generators.Tests/ServiceRequirementsGeneratorTests.cs</tests>
[Generator]
public class ServiceRequirementsGenerator : IIncrementalGenerator {

  private static readonly string[] _registrationMethods = [
    "AddSingleton", "AddScoped", "AddTransient",
    "TryAddSingleton", "TryAddScoped", "TryAddTransient",
    "AddHostedService",
  ];

  private const string ADD_HOSTED_SERVICE = "AddHostedService";
  private const string HOSTED_SERVICE_TYPE = "global::Microsoft.Extensions.Hosting.IHostedService";

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var typeRegistrations = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => _isCandidateRegistration(node),
        transform: static (ctx, ct) => _extractRequirement(ctx, ct))
      .Where(static r => r is not null)
      .Select(static (r, _) => r!);

    // A factory registration has no implementation type, so a type-based scan cannot see it at all.
    // That is where services are hand-constructed, which is exactly the population this check
    // exists for: a required dependency that a factory resolves and nothing registers throws only
    // when something first resolves the service, which may be never in a test run and always in
    // production. What the factory resolves IS the requirement.
    var factoryRegistrations = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => _isFactoryRegistration(node),
        transform: static (ctx, ct) => _extractFactoryRequirement(ctx, ct))
      .Where(static r => r is not null)
      .Select(static (r, _) => r!);

    var registrations = typeRegistrations.Collect()
      .Combine(factoryRegistrations.Collect())
      .Select(static (pair, _) => pair.Left.AddRange(pair.Right));

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
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Recognizes every registration shape the container API offers; the shapes are the specification.")]
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
    var implementationSyntax = args[^1];
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
      // An optional parameter has a compiler-supplied default, so the composition is not obliged
      // to provide it. Recording one would make validation demand a registration for every
      // dependency that legitimately falls back and, because validation runs at startup by
      // default, would fail correct applications on boot.
      if (p.IsOptional) {
        continue;
      }
      // The container always satisfies IEnumerable<T>, returning empty when nothing is
      // registered, so requiring one would report a gap that cannot exist.
      if (p.Type is INamedTypeSymbol { IsGenericType: true } collection
          && collection.ConstructedFrom.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) {
        continue;
      }
      // Only interfaces are container-resolved services here. Demanding a registration for a
      // string, an int, or a token would fail every composition, and a guard that always fails is
      // a guard that gets switched off.
      if (p.Type.TypeKind != TypeKind.Interface) {
        continue;
      }
      if (p.Type is not INamedTypeSymbol dependency || !_isReferenceable(dependency)) {
        continue;
      }
      dependencies.Add(TypeNameUtilities.FullyQualified(p.Type));
    }

    if (dependencies.Count == 0) {
      return null;
    }

    // Validation applies a requirement only when its implementation is registered, and reads that
    // from the descriptors. A call with arguments may build the implementation through a factory,
    // whose descriptor carries only the service type, so record the service type it will carry.
    // Recording it for an instance argument too costs nothing: that descriptor is also matched by
    // its instance's type.
    var factoryServiceTypes = new List<string>();
    if (invocation.ArgumentList.Arguments.Count > 0) {
      var serviceType = _factoryServiceType(ctx, generic, ct);
      var implementationName = TypeNameUtilities.FullyQualified(implementation);
      if (serviceType is not null && !string.Equals(serviceType, implementationName, System.StringComparison.Ordinal)) {
        factoryServiceTypes.Add(serviceType);
      }
    }

    return new RequirementInfo(
      TypeNameUtilities.FullyQualified(implementation),
      dependencies,
      factoryServiceTypes);
  }

  /// <summary>
  /// The service type a registration's descriptor will carry, or null when it cannot be named.
  /// </summary>
  /// <remarks>
  /// AddHostedService registers under <c>IHostedService</c> whatever its type argument is; every
  /// other registration method registers under its first type argument.
  /// </remarks>
  private static string? _factoryServiceType(
      GeneratorSyntaxContext ctx, GenericNameSyntax generic, CancellationToken ct) {
    if (string.Equals(generic.Identifier.ValueText, ADD_HOSTED_SERVICE, System.StringComparison.Ordinal)) {
      return HOSTED_SERVICE_TYPE;
    }
    if (ctx.SemanticModel.GetSymbolInfo(generic.TypeArgumentList.Arguments[0], ct).Symbol
        is not INamedTypeSymbol serviceType || !_isReferenceable(serviceType)) {
      return null;
    }
    return TypeNameUtilities.FullyQualified(serviceType);
  }

  /// <summary>
  /// Cheap syntactic filter: a registration invocation whose argument is a lambda.
  /// </summary>
  private static bool _isFactoryRegistration(SyntaxNode node) {
    if (node is not InvocationExpressionSyntax invocation) {
      return false;
    }
    if (invocation.Expression is not MemberAccessExpressionSyntax member) {
      return false;
    }

    var name = member.Name is GenericNameSyntax g ? g.Identifier.ValueText : member.Name.Identifier.ValueText;
    var known = false;
    for (var i = 0; i < _registrationMethods.Length; i++) {
      if (string.Equals(name, _registrationMethods[i], System.StringComparison.Ordinal)) {
        known = true;
        break;
      }
    }
    if (!known) {
      return false;
    }

    foreach (var arg in invocation.ArgumentList.Arguments) {
      if (arg.Expression is SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax) {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Records the services a registration factory resolves as requirements of that registration.
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Recognizes every factory shape a registration can take; the shapes are the specification.")]
  private static RequirementInfo? _extractFactoryRequirement(GeneratorSyntaxContext ctx, CancellationToken ct) {
    ct.ThrowIfCancellationRequested();

    var invocation = (InvocationExpressionSyntax)ctx.Node;
    var member = (MemberAccessExpressionSyntax)invocation.Expression;

    // Name the requirement after the service being registered, so a failure says which
    // registration is unsatisfiable rather than naming a lambda.
    if (member.Name is not GenericNameSyntax generic || !generic.TypeArgumentList.Arguments.Any()) {
      return null;
    }
    if (ctx.SemanticModel.GetSymbolInfo(generic.TypeArgumentList.Arguments[0], ct).Symbol
        is not INamedTypeSymbol serviceType || !_isReferenceable(serviceType)) {
      return null;
    }
    var owner = TypeNameUtilities.FullyQualified(serviceType);

    var dependencies = new List<string>();
    foreach (var call in invocation.DescendantNodes().OfType<InvocationExpressionSyntax>()) {
      if (call.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax resolve }) {
        continue;
      }
      // Only GetRequiredService is a hard requirement. GetService returning null is a documented
      // outcome the caller has already accounted for, and demanding a registration for it would
      // fail correct compositions.
      if (!string.Equals(resolve.Identifier.ValueText, "GetRequiredService", System.StringComparison.Ordinal)) {
        continue;
      }
      if (resolve.TypeArgumentList.Arguments.Count != 1) {
        continue;
      }
      if (ctx.SemanticModel.GetSymbolInfo(resolve.TypeArgumentList.Arguments[0], ct).Symbol
          is not INamedTypeSymbol dep || dep.TypeKind != TypeKind.Interface || !_isReferenceable(dep)) {
        continue;
      }
      dependencies.Add(TypeNameUtilities.FullyQualified(dep));
    }

    if (dependencies.Count == 0) {
      return null;
    }

    // The requirement is named after the type argument, which is the descriptor's service type for
    // every registration method but AddHostedService: that one registers under IHostedService, so
    // record it or the factory registration would never be recognised as present.
    var factoryServiceTypes = new List<string>();
    if (string.Equals(generic.Identifier.ValueText, ADD_HOSTED_SERVICE, System.StringComparison.Ordinal)) {
      factoryServiceTypes.Add(HOSTED_SERVICE_TYPE);
    }
    return new RequirementInfo(owner, dependencies, factoryServiceTypes);
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

    // A generic argument can be inaccessible even when the constructed type's own declaration is
    // fine: ILensQuery<PrivateTestModel> names a type the generated file cannot see, and emitting
    // it turns this guard into a build break for everyone.
    foreach (var argument in type.TypeArguments) {
      if (argument is INamedTypeSymbol named && !_isReferenceable(named)) {
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
    sb.AppendLine("/// Constructor dependencies of every type a registration call in this compilation registers.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("internal static class WhizbangServiceRequirements {");
    sb.AppendLine();
    sb.AppendLine("  /// <summary>Every registered implementation type and what it requires.</summary>");
    sb.AppendLine("  internal static readonly global::Whizbang.Core.DependencyInjection.ServiceRequirement[] All = [");

    // Two registrations of the same type contribute the same requirement; emitting it twice would
    // report the same gap twice and overstate how many things are broken. The service types its
    // factory registrations were made under are merged, though: a composition may make any one of
    // those registrations, and each must be recognised as registering the type.
    foreach (var group in items
        .GroupBy(i => i.ImplementationType, System.StringComparer.Ordinal)
        .OrderBy(g => g.Key, System.StringComparer.Ordinal)) {
      var item = group.First();
      var factoryServiceTypes = group
        .SelectMany(i => i.FactoryServiceTypes)
        .Distinct(System.StringComparer.Ordinal)
        .OrderBy(t => t, System.StringComparer.Ordinal)
        .ToList();

      sb.Append("    new global::Whizbang.Core.DependencyInjection.ServiceRequirement(typeof(").Append(item.ImplementationType).AppendLine("), [");
      foreach (var dep in item.Dependencies) {
        sb.Append("      typeof(").Append(dep).AppendLine("),");
      }
      if (factoryServiceTypes.Count == 0) {
        sb.AppendLine("    ]),");
        continue;
      }
      sb.AppendLine("    ], [");
      foreach (var serviceType in factoryServiceTypes) {
        sb.Append("      typeof(").Append(serviceType).AppendLine("),");
      }
      sb.AppendLine("    ]),");
    }

    sb.AppendLine("  ];");
    sb.AppendLine("}");

    spc.AddSource("WhizbangServiceRequirements.g.cs", sb.ToString());
  }

  /// <summary>
  /// One registered implementation, the service types its constructor requires, and the service
  /// types any factory registration of it is made under.
  /// </summary>
  private sealed record RequirementInfo(
    string ImplementationType,
    List<string> Dependencies,
    List<string> FactoryServiceTypes);
}
