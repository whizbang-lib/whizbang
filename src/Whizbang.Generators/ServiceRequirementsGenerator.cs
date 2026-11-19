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
/// <para>
/// A factory that builds its service only when another service is registered, else a stand-in, is read the same way
/// (#1907): what its building branch needs is recorded under that condition, so a host without the service is not
/// failed for it and a host with it is checked for all of it. Nothing is declared for this either: the condition is
/// the factory's own code.
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

  /// <summary>
  /// The <c>ServiceDescriptor</c> factory methods read as registrations (#1907): a descriptor built with one type argument
  /// and a factory, as <c>services.TryAdd(ServiceDescriptor.Singleton&lt;T&gt;(sp =&gt; ...))</c> registers it. With two type
  /// arguments the descriptor's service type is one many registrations share (an <c>IHostedService</c>, a step
  /// interface), so its requirement would apply wherever any of them is registered; those are not read.
  /// </summary>
  private static readonly string[] _descriptorLifetimes = ["Singleton", "Scoped", "Transient"];

  private const string SERVICE_DESCRIPTOR = "ServiceDescriptor";
  private const string SERVICE_DESCRIPTOR_TYPE = "global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor";
  private const string GET_SERVICE = "GetService";

  private const string ADD_HOSTED_SERVICE = "AddHostedService";
  private const string HOSTED_SERVICE_TYPE = "global::Microsoft.Extensions.Hosting.IHostedService";

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var typeRegistrations = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsRegistrationCallCandidate(node),
        transform: static (ctx, ct) => _extractRequirement(ctx, ct))
      .Where(static r => r is not null)
      .Select(static (r, _) => r!);

    // A factory registration has no implementation type, so a type-based scan cannot see it at all.
    // That is where services are hand-constructed, which is exactly the population this check
    // exists for: a required dependency that a factory resolves and nothing registers throws only
    // when something first resolves the service, which may be never in a test run and always in
    // production. What the factory resolves IS the requirement.
    var factoryRegistrations = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsFactoryRegistrationCandidate(node),
        transform: static (ctx, ct) => _extractFactoryRequirements(ctx, ct))
      .SelectMany(static (r, _) => r);

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
  /// <remarks>Public so the build-cost guard can run it over ordinary code (#1650).</remarks>
  /// <tests>tests/Whizbang.Generators.Component.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsRegistrationCallCandidate(SyntaxNode node) {
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
    if (!IsReferenceable(implementation)) {
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
      if (p.Type is not INamedTypeSymbol dependency || !IsReferenceable(dependency)) {
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
    if (invocation.ArgumentList.Arguments.Any()) {
      var serviceType = _factoryServiceType(ctx, generic, ct);
      var implementationName = TypeNameUtilities.FullyQualified(implementation);
      if (serviceType is not null && !string.Equals(serviceType, implementationName, System.StringComparison.Ordinal)) {
        factoryServiceTypes.Add(serviceType);
      }
    }

    return new RequirementInfo(
      TypeNameUtilities.FullyQualified(implementation),
      dependencies,
      factoryServiceTypes,
      _constructionCondition(ctx, invocation, implementation, ct));
  }

  /// <summary>
  /// The service whose registration a factory registration builds its implementation under (#1907), or null when it
  /// builds it unconditionally or the generator cannot tell.
  /// </summary>
  /// <remarks>
  /// Recognized: an expression-bodied factory that is a conditional on whether a service resolves, which constructs the
  /// implementation with <c>new</c> in the branch taken when the service is registered and not in the other, as
  /// <c>sp =&gt; sp.GetService&lt;X&gt;() is { } x ? new T(x, ...) : null!</c> does. Every other shape keeps the
  /// constructor's requirement unconditional: erring towards checking.
  /// </remarks>
  /// <docs>operations/dependency-injection/registration-validation#conditional-factory-registrations</docs>
  /// <tests>tests/Whizbang.Generators.Tests/ServiceRequirementsGeneratorTests.cs:ARegistrationMethodFactoryThatBuildsOnlyUnderACondition_RequiresTheConstructorUnderItTooAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/ServiceRequirementsGeneratorTests.cs:AFactoryThatDoesNotBuildItsTypeOnlyUnderTheCondition_RequiresTheConstructorUnconditionallyAsync</tests>
  private static string? _constructionCondition(
      GeneratorSyntaxContext ctx, InvocationExpressionSyntax invocation, INamedTypeSymbol implementation, CancellationToken ct) {
    foreach (var argument in invocation.ArgumentList.Arguments) {
      if (argument.Expression is not LambdaExpressionSyntax { ExpressionBody: { } body }
          || _unwrap(body) is not ConditionalExpressionSyntax conditional
          || _readCondition(ctx, conditional.Condition, ct) is not { } condition) {
        continue;
      }
      var (present, absent) = condition.PresentWhenTrue
        ? (conditional.WhenTrue, conditional.WhenFalse)
        : (conditional.WhenFalse, conditional.WhenTrue);
      if (_constructs(ctx, present, implementation, ct) && !_constructs(ctx, absent, implementation, ct)) {
        return condition.Service;
      }
    }
    return null;
  }

  /// <summary>True when the expression is a <c>new</c> of exactly <paramref name="implementation"/>.</summary>
  private static bool _constructs(
      GeneratorSyntaxContext ctx, ExpressionSyntax expression, INamedTypeSymbol implementation, CancellationToken ct) =>
    _unwrap(expression) is BaseObjectCreationExpressionSyntax creation
    && SymbolEqualityComparer.Default.Equals(ctx.SemanticModel.GetTypeInfo(creation, ct).Type, implementation);

  /// <summary>The expression inside any parentheses around it.</summary>
  private static ExpressionSyntax _unwrap(ExpressionSyntax expression) {
    while (expression is ParenthesizedExpressionSyntax parenthesized) {
      expression = parenthesized.Expression;
    }
    return expression;
  }

  /// <summary>
  /// A conditional's test of whether one service is registered: the service, and which branch is taken when it is.
  /// </summary>
  /// <param name="Service">The tested service type, fully qualified.</param>
  /// <param name="PresentWhenTrue">True when the condition holds only if the service resolved.</param>
  private readonly record struct RegistrationCondition(string Service, bool PresentWhenTrue);

  /// <summary>
  /// Reads a condition that tests whether <c>GetService&lt;X&gt;()</c> resolved: a pattern that matches only a resolved
  /// service (<c>is { }</c>, <c>is X x</c>) or only an unresolved one (<c>is null</c>), either negated, or a comparison
  /// with <c>null</c>. Null for anything else, which says nothing certain about the registration.
  /// </summary>
  private static RegistrationCondition? _readCondition(GeneratorSyntaxContext ctx, ExpressionSyntax condition, CancellationToken ct) {
    condition = _unwrap(condition);
    if (condition is IsPatternExpressionSyntax test) {
      var service = _resolvedService(ctx, test.Expression, ct);
      var matchesResolved = _matchesOnlyResolved(test.Pattern);
      return service is null || matchesResolved is null ? null : new RegistrationCondition(service, matchesResolved.Value);
    }
    if (condition is BinaryExpressionSyntax comparison
        && comparison.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression) {
      string? service = null;
      if (_isNull(comparison.Right)) {
        service = _resolvedService(ctx, comparison.Left, ct);
      } else if (_isNull(comparison.Left)) {
        service = _resolvedService(ctx, comparison.Right, ct);
      }
      return service is null ? null : new RegistrationCondition(service, comparison.Kind() == SyntaxKind.NotEqualsExpression);
    }
    return null;
  }

  /// <summary>
  /// True when the pattern matches only a resolved service, false when it matches only an unresolved one, null when it
  /// can match either or the generator cannot tell.
  /// </summary>
  private static bool? _matchesOnlyResolved(PatternSyntax pattern) => pattern switch {
    // `not` is the only unary pattern.
    UnaryPatternSyntax negated => !_matchesOnlyResolved(negated.Pattern),
    ConstantPatternSyntax constant => _isNull(constant.Expression) ? false : null,
    RecursivePatternSyntax or DeclarationPatternSyntax => true,
    _ => null,
  };

  private static bool _isNull(ExpressionSyntax expression) =>
    _unwrap(expression).IsKind(SyntaxKind.NullLiteralExpression);

  /// <summary>
  /// The service a <c>GetService&lt;X&gt;()</c> call resolves, fully qualified, or null when the expression is not such a
  /// call or names a type the manifest cannot.
  /// </summary>
  private static string? _resolvedService(GeneratorSyntaxContext ctx, ExpressionSyntax expression, CancellationToken ct) {
    // A resolve with no arguments and one type argument: sp.GetService<X>().
    if (_unwrap(expression) is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } call
        || call.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax resolve }
        || resolve.TypeArgumentList.Arguments.Count != 1
        || !string.Equals(resolve.Identifier.ValueText, GET_SERVICE, System.StringComparison.Ordinal)) {
      return null;
    }
    return ctx.SemanticModel.GetSymbolInfo(resolve.TypeArgumentList.Arguments[0], ct).Symbol
        is INamedTypeSymbol service && service.TypeKind != TypeKind.Error && IsReferenceable(service)
      ? TypeNameUtilities.FullyQualified(service)
      : null;
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
        is not INamedTypeSymbol serviceType || !IsReferenceable(serviceType)) {
      return null;
    }
    return TypeNameUtilities.FullyQualified(serviceType);
  }

  /// <summary>
  /// Cheap syntactic filter: a registration invocation, or a <c>ServiceDescriptor</c> factory method (#1907), whose
  /// argument is a lambda.
  /// </summary>
  /// <remarks>Public so the build-cost guard can run it over ordinary code (#1650).</remarks>
  /// <tests>tests/Whizbang.Generators.Component.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsFactoryRegistrationCandidate(SyntaxNode node) {
    if (node is not InvocationExpressionSyntax invocation) {
      return false;
    }
    if (invocation.Expression is not MemberAccessExpressionSyntax member) {
      return false;
    }

    var name = member.Name is GenericNameSyntax g ? g.Identifier.ValueText : member.Name.Identifier.ValueText;
    if (!_isOneOf(name, _registrationMethods) && !_isDescriptorFactory(member, name)) {
      return false;
    }

    foreach (var arg in invocation.ArgumentList.Arguments) {
      if (arg.Expression is SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax) {
        return true;
      }
    }
    return false;
  }

  /// <summary>Syntactically, <c>ServiceDescriptor.Singleton</c>, <c>.Scoped</c> or <c>.Transient</c>.</summary>
  private static bool _isDescriptorFactory(MemberAccessExpressionSyntax member, string name) =>
    _isOneOf(name, _descriptorLifetimes)
    && member.Expression is IdentifierNameSyntax { Identifier.ValueText: SERVICE_DESCRIPTOR }
      or MemberAccessExpressionSyntax { Name.Identifier.ValueText: SERVICE_DESCRIPTOR };

  private static bool _isOneOf(string name, string[] names) {
    for (var i = 0; i < names.Length; i++) {
      if (string.Equals(name, names[i], System.StringComparison.Ordinal)) {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Records the services a registration factory resolves as requirements of that registration: one requirement for
  /// what it always resolves, and one for each service whose registration a branch of it is built under (#1907).
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Recognizes every factory shape a registration can take; the shapes are the specification.")]
  private static ImmutableArray<RequirementInfo> _extractFactoryRequirements(GeneratorSyntaxContext ctx, CancellationToken ct) {
    ct.ThrowIfCancellationRequested();

    var invocation = (InvocationExpressionSyntax)ctx.Node;
    var member = (MemberAccessExpressionSyntax)invocation.Expression;

    // Name the requirement after the service being registered, so a failure says which
    // registration is unsatisfiable rather than naming a lambda.
    if (member.Name is not GenericNameSyntax generic || !generic.TypeArgumentList.Arguments.Any()) {
      return [];
    }
    // A descriptor names the service it registers only when it has one type argument, and only the container's own
    // ServiceDescriptor builds one; a consumer's method of the same name is not a registration.
    if (_isDescriptorFactory(member, generic.Identifier.ValueText)
        && (generic.TypeArgumentList.Arguments.Count != 1
            || ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method
            || TypeNameUtilities.FullyQualified(method.ContainingType) != SERVICE_DESCRIPTOR_TYPE)) {
      return [];
    }
    if (ctx.SemanticModel.GetSymbolInfo(generic.TypeArgumentList.Arguments[0], ct).Symbol
        is not INamedTypeSymbol serviceType || !IsReferenceable(serviceType)) {
      return [];
    }
    var owner = TypeNameUtilities.FullyQualified(serviceType);

    // Grouped by the service each call is resolved under: null for a call the factory always makes.
    var dependencies = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
    var unconditional = new List<string>();
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
          is not INamedTypeSymbol dep || dep.TypeKind != TypeKind.Interface || !IsReferenceable(dep)) {
        continue;
      }
      var condition = _branchCondition(ctx, call, invocation, ct);
      var name = TypeNameUtilities.FullyQualified(dep);
      if (condition is null) {
        unconditional.Add(name);
        continue;
      }
      if (!dependencies.TryGetValue(condition, out var bucket)) {
        bucket = [];
        dependencies[condition] = bucket;
      }
      bucket.Add(name);
    }

    // The requirement is named after the type argument, which is the descriptor's service type for
    // every registration method but AddHostedService: that one registers under IHostedService, so
    // record it or the factory registration would never be recognized as present.
    var factoryServiceTypes = new List<string>();
    if (string.Equals(generic.Identifier.ValueText, ADD_HOSTED_SERVICE, System.StringComparison.Ordinal)) {
      factoryServiceTypes.Add(HOSTED_SERVICE_TYPE);
    }

    var requirements = ImmutableArray.CreateBuilder<RequirementInfo>();
    if (unconditional.Count > 0) {
      requirements.Add(new RequirementInfo(owner, unconditional, factoryServiceTypes, null));
    }
    foreach (var conditional in dependencies) {
      requirements.Add(new RequirementInfo(owner, conditional.Value, factoryServiceTypes, conditional.Key));
    }
    return requirements.ToImmutable();
  }

  /// <summary>
  /// The service a resolve call inside a factory is made under (#1907): the innermost conditional around it that tests
  /// whether a service is registered, when the call is in the branch taken if it is. Null for a call the factory makes
  /// either way, or in a branch taken when the service is absent, which stays unconditional: erring towards checking.
  /// </summary>
  /// <docs>operations/dependency-injection/registration-validation#conditional-factory-registrations</docs>
  /// <tests>tests/Whizbang.Generators.Tests/ServiceRequirementsGeneratorTests.cs:AFactoryThatBuildsOnlyWhenAServiceIsRegistered_RequiresItsBuildingBranch_OnlyUnderThatConditionAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/ServiceRequirementsGeneratorTests.cs:TheStandInBranch_IsRequiredUnconditionally_AndTheBuildingBranchUnderItsConditionAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/ServiceRequirementsGeneratorTests.cs:AConditionThatDoesNotSayWhetherAServiceIsRegistered_LeavesTheRequirementUnconditionalAsync</tests>
  private static string? _branchCondition(
      GeneratorSyntaxContext ctx, SyntaxNode call, InvocationExpressionSyntax registration, CancellationToken ct) {
    // The call is inside the registration, so walking up always reaches it.
    for (var node = call; node != registration; node = node.Parent!) {
      if (node.Parent is not ConditionalExpressionSyntax conditional
          || node == conditional.Condition
          || _readCondition(ctx, conditional.Condition, ct) is not { } condition) {
        continue;
      }
      if ((node == conditional.WhenTrue) == condition.PresentWhenTrue) {
        return condition.Service;
      }
    }
    return null;
  }

  /// <summary>
  /// True when generated source in the same assembly can name this type.
  /// </summary>
  /// <remarks>Shared with <see cref="ConfigurationManifestGenerator"/>, whose manifest also names types.</remarks>
  internal static bool IsReferenceable(INamedTypeSymbol type) {
    for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType) {
      if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)) {
        return false;
      }
    }

    // A generic argument can be inaccessible even when the constructed type's own declaration is
    // fine: ILensQuery<PrivateTestModel> names a type the generated file cannot see, and emitting
    // it turns this guard into a build break for everyone.
    foreach (var argument in type.TypeArguments) {
      if (argument is INamedTypeSymbol named && !IsReferenceable(named)) {
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

    // Two registrations of the same type under the same condition contribute one requirement;
    // emitting it twice would report the same gap twice and overstate how many things are broken.
    // What each requires is merged, so no registration's dependencies are lost to another's (#1907),
    // and so are the service types its factory registrations were made under: a composition may make
    // any one of those registrations, and each must be recognized as registering the type.
    foreach (var group in items
        .GroupBy(i => (i.ImplementationType, Condition: i.Condition ?? ""))
        .OrderBy(g => g.Key.ImplementationType, System.StringComparer.Ordinal)
        .ThenBy(g => g.Key.Condition, System.StringComparer.Ordinal)) {
      var dependencies = group
        .SelectMany(i => i.Dependencies)
        .Distinct(System.StringComparer.Ordinal)
        .ToList();
      var factoryServiceTypes = group
        .SelectMany(i => i.FactoryServiceTypes)
        .Distinct(System.StringComparer.Ordinal)
        .OrderBy(t => t, System.StringComparer.Ordinal)
        .ToList();

      sb.Append("    new global::Whizbang.Core.DependencyInjection.ServiceRequirement(typeof(").Append(group.Key.ImplementationType).AppendLine("), [");
      foreach (var dep in dependencies) {
        sb.Append("      typeof(").Append(dep).AppendLine("),");
      }
      if (factoryServiceTypes.Count == 0) {
        sb.Append("    ])");
      } else {
        sb.AppendLine("    ], [");
        foreach (var serviceType in factoryServiceTypes) {
          sb.Append("      typeof(").Append(serviceType).AppendLine("),");
        }
        sb.Append("    ])");
      }
      // A requirement made under a condition applies only to a composition that registers it (#1907).
      if (group.Key.Condition.Length > 0) {
        sb.Append(" { Condition = typeof(").Append(group.Key.Condition).Append(") }");
      }
      sb.AppendLine(",");
    }

    sb.AppendLine("  ];");
    sb.AppendLine("}");

    spc.AddSource("WhizbangServiceRequirements.g.cs", sb.ToString());
  }

  /// <summary>
  /// One registered implementation, the service types its constructor requires, the service
  /// types any factory registration of it is made under, and the service whose registration the
  /// requirement depends on (#1907), or null when it always applies.
  /// </summary>
  private sealed record RequirementInfo(
    string ImplementationType,
    List<string> Dependencies,
    List<string> FactoryServiceTypes,
    string? Condition);
}
