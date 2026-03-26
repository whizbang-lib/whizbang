using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_UserLensInterface_RegistersInterfaceToImplementationAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_UserPerspectiveInterface_RegistersInterfaceToImplementationAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_SelfRegistration_EnabledByDefault_RegistersBothAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_AbstractLens_SkipsRegistrationAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_AbstractBaseWithConcreteChild_RegistersOnlyChildAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_DirectWhizbangImplementation_SkippedAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_MultipleLenses_RegistersAllAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_CombinedLensAndPerspective_GeneratesBothMethodsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_NoUserInterfaces_GeneratesEmptyMethodsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_NestedUserInterface_RegistersWithFullNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_OptionsClass_GeneratedCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_ReportsInfoDiagnostic_WhenServiceDiscoveredAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs:Generator_ReportsInfoDiagnostic_WhenAbstractClassSkippedAsync</tests>
/// Incremental source generator that discovers user interfaces extending Whizbang interfaces
/// (ILensQuery, IPerspectiveFor) and generates DI service registration code.
/// This enables auto-registration of user-defined lens and perspective services.
/// </summary>
[Generator]
public class ServiceRegistrationGenerator : IIncrementalGenerator {
  private const string LENS_QUERY_INTERFACE = "Whizbang.Core.Lenses.ILensQuery";
  private const string PERSPECTIVE_BASE_INTERFACE = "Whizbang.Core.Perspectives.IPerspectiveBase";
  private const string PERSPECTIVE_FOR_INTERFACE = "Whizbang.Core.Perspectives.IPerspectiveFor";
  private const string PERSPECTIVE_WITH_ACTIONS_FOR_INTERFACE = "Whizbang.Core.Perspectives.IPerspectiveWithActionsFor";
  private const string TEMPLATE_SNIPPET_FILE = "ServiceRegistrationSnippets.cs";
  private const string PLACEHOLDER_USER_INTERFACE = "__USER_INTERFACE__";
  private const string PLACEHOLDER_CONCRETE_CLASS = "__CONCRETE_CLASS__";
  private const string REGION_NAMESPACE = "NAMESPACE";
  private const string DEFAULT_NAMESPACE = "Whizbang.Core";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Single pipeline: discover classes that implement user interfaces extending Whizbang interfaces
    var serviceCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractServiceRegistrationInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Combine with compilation to get assembly name for namespace
    var compilationAndServices = context.CompilationProvider.Combine(serviceCandidates.Collect());

    context.RegisterSourceOutput(
        compilationAndServices,
        static (ctx, data) => {
          var compilation = data.Left;
          var services = data.Right;
          _generateServiceRegistrations(ctx, compilation, services!);
        }
    );
  }

  /// <summary>
  /// Extracts service registration information from a class declaration.
  /// Supports two patterns:
  /// 1. User-defined interface pattern: class implements IMyLens where IMyLens : ILensQuery&lt;T&gt;
  /// 2. Direct implementation pattern: class implements ILensQuery&lt;T&gt; directly
  /// Returns null if the class doesn't match either pattern.
  /// </summary>
  private static ServiceRegistrationInfo? _extractServiceRegistrationInfo(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Skip abstract classes - they can't be instantiated
    // But we still want to analyze them to report WHIZ041 diagnostic later
    var isAbstract = classSymbol.IsAbstract;

    // Skip types that are not accessible from outside their declaring type
    // This handles private nested classes inside test fixtures
    if (!_isTypeAccessible(classSymbol)) {
      return null;
    }

    // Skip Whizbang.Core internal classes - this generator is for user types only
    var className = classSymbol.ToDisplayString();
    if (className.StartsWith("Whizbang.Core", StringComparison.Ordinal)) {
      return null;
    }

    // Pattern 1: Find user interfaces that extend Whizbang interfaces
    // A "user interface" is one that:
    // 1. Is NOT a Whizbang interface itself (doesn't start with Whizbang.Core)
    // 2. Has ILensQuery or IPerspectiveFor in its AllInterfaces hierarchy
    var userInterface = classSymbol.Interfaces.FirstOrDefault(i => _isUserInterfaceExtendingWhizbang(i));

    if (userInterface is not null) {
      // User-defined interface pattern - register against user interface
      var category = _getServiceCategory(userInterface);
      return new ServiceRegistrationInfo(
          ConcreteTypeName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          SimpleTypeName: TypeNameUtilities.GetSimpleName(classSymbol),
          UserInterfaceName: userInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          Category: category,
          IsAbstract: isAbstract
      );
    }

    // Pattern 2: Direct implementation of ILensQuery<T> or IPerspectiveFor<T>
    var directWhizbangInterface = classSymbol.Interfaces.FirstOrDefault(i => _isDirectWhizbangInterface(i));

    if (directWhizbangInterface is not null) {
      // Direct implementation pattern - register against the Whizbang interface
      var category = _getServiceCategoryFromWhizbangInterface(directWhizbangInterface);
      return new ServiceRegistrationInfo(
          ConcreteTypeName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          SimpleTypeName: TypeNameUtilities.GetSimpleName(classSymbol),
          UserInterfaceName: directWhizbangInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          Category: category,
          IsAbstract: isAbstract
      );
    }

    return null;
  }

  /// <summary>
  /// Checks if an interface is a direct Whizbang interface (ILensQuery or IPerspectiveFor).
  /// Only matches closed generic types (e.g., ILensQuery&lt;Order&gt;), not open generics (e.g., ILensQuery&lt;TModel&gt;).
  /// </summary>
  private static bool _isDirectWhizbangInterface(INamedTypeSymbol interfaceSymbol) {
    var name = interfaceSymbol.OriginalDefinition.ToDisplayString();
    var isWhizbangInterface = name.StartsWith(LENS_QUERY_INTERFACE, StringComparison.Ordinal) ||
                              name.StartsWith(PERSPECTIVE_FOR_INTERFACE, StringComparison.Ordinal) ||
                              name.StartsWith(PERSPECTIVE_WITH_ACTIONS_FOR_INTERFACE, StringComparison.Ordinal);

    if (!isWhizbangInterface) {
      return false;
    }

    // Filter out open generic types - all type arguments must be concrete types
    // e.g., ILensQuery<Order> is OK, ILensQuery<TModel> is not
    if (interfaceSymbol.IsGenericType) {
      foreach (var typeArg in interfaceSymbol.TypeArguments) {
        if (typeArg.TypeKind == TypeKind.TypeParameter) {
          return false; // Open generic - skip
        }
      }
    }

    return true;
  }

  /// <summary>
  /// Gets the service category from a direct Whizbang interface.
  /// </summary>
  private static ServiceCategory _getServiceCategoryFromWhizbangInterface(INamedTypeSymbol whizbangInterface) {
    var name = whizbangInterface.OriginalDefinition.ToDisplayString();
    if (name.StartsWith(PERSPECTIVE_FOR_INTERFACE, StringComparison.Ordinal) ||
        name.StartsWith(PERSPECTIVE_WITH_ACTIONS_FOR_INTERFACE, StringComparison.Ordinal)) {
      return ServiceCategory.Perspective;
    }
    return ServiceCategory.Lens;
  }

  /// <summary>
  /// Checks if an interface is a "user interface" that extends a Whizbang interface.
  /// A user interface:
  /// 1. Is NOT defined in Whizbang.Core namespace
  /// 2. Extends ILensQuery or IPerspectiveFor
  /// </summary>
  private static bool _isUserInterfaceExtendingWhizbang(INamedTypeSymbol interfaceSymbol) {
    // Check if interface is from user code (not Whizbang.Core)
    var interfaceName = interfaceSymbol.ToDisplayString();
    if (interfaceName.StartsWith("Whizbang.Core", StringComparison.Ordinal)) {
      return false;
    }

    // Check if it extends ILensQuery or IPerspectiveFor
    return interfaceSymbol.AllInterfaces.Any(i => {
      var name = i.OriginalDefinition.ToDisplayString();
      return name.StartsWith(LENS_QUERY_INTERFACE, StringComparison.Ordinal) ||
             name.StartsWith(PERSPECTIVE_BASE_INTERFACE, StringComparison.Ordinal);
    });
  }

  /// <summary>
  /// Determines the service category (Lens or Perspective) from a user interface.
  /// </summary>
  private static ServiceCategory _getServiceCategory(INamedTypeSymbol userInterface) {
    foreach (var baseInterface in userInterface.AllInterfaces) {
      var name = baseInterface.OriginalDefinition.ToDisplayString();
      if (name.StartsWith(PERSPECTIVE_BASE_INTERFACE, StringComparison.Ordinal)) {
        return ServiceCategory.Perspective;
      }
      if (name.StartsWith(LENS_QUERY_INTERFACE, StringComparison.Ordinal)) {
        return ServiceCategory.Lens;
      }
    }

    // Default to Lens (shouldn't happen if _isUserInterfaceExtendingWhizbang returned true)
    return ServiceCategory.Lens;
  }

  /// <summary>
  /// Checks if a type is accessible from outside its declaring type.
  /// Returns false for private/protected nested types.
  /// </summary>
  private static bool _isTypeAccessible(INamedTypeSymbol typeSymbol) {
    // Check the type itself
    if (typeSymbol.DeclaredAccessibility == Accessibility.Private ||
        typeSymbol.DeclaredAccessibility == Accessibility.Protected ||
        typeSymbol.DeclaredAccessibility == Accessibility.ProtectedAndInternal) {
      return false;
    }

    // Check all containing types (for nested types)
    var containingType = typeSymbol.ContainingType;
    while (containingType is not null) {
      if (containingType.DeclaredAccessibility == Accessibility.Private ||
          containingType.DeclaredAccessibility == Accessibility.Protected ||
          containingType.DeclaredAccessibility == Accessibility.ProtectedAndInternal) {
        return false;
      }
      containingType = containingType.ContainingType;
    }

    return true;
  }

  /// <summary>
  /// Generates the service registration source code.
  /// </summary>
  private static void _generateServiceRegistrations(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<ServiceRegistrationInfo> services) {

    var assemblyName = compilation.AssemblyName ?? DEFAULT_NAMESPACE;
    var namespaceName = $"{assemblyName}.Generated";

    // Separate concrete from abstract services
    var concreteServices = services.Where(s => !s.IsAbstract).ToImmutableArray();
    var abstractServices = services.Where(s => s.IsAbstract).ToImmutableArray();

    // Report diagnostics for abstract classes
    foreach (var abstractService in abstractServices) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.AbstractClassSkipped,
          Location.None,
          abstractService.SimpleTypeName,
          abstractService.UserInterfaceName
      ));
    }

    // Report diagnostics for discovered services
    foreach (var service in concreteServices) {
      var categoryName = service.Category == ServiceCategory.Perspective ? "Perspective" : "Lens";
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.UserServiceDiscovered,
          Location.None,
          categoryName,
          service.SimpleTypeName,
          service.UserInterfaceName
      ));
    }

    // Split by category
    var lensServices = concreteServices.Where(s => s.Category == ServiceCategory.Lens).ToImmutableArray();
    var perspectiveServices = concreteServices.Where(s => s.Category == ServiceCategory.Perspective).ToImmutableArray();

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(ServiceRegistrationGenerator).Assembly,
        "ServiceRegistrationsTemplate.cs"
    );

    // Load snippets
    var interfaceSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ServiceRegistrationGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "INTERFACE_REGISTRATION_SNIPPET"
    );

    var selfSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ServiceRegistrationGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "SELF_REGISTRATION_SNIPPET"
    );

    // Generate lens registrations
    var lensRegistrations = _generateRegistrationCode(lensServices, interfaceSnippet, selfSnippet);

    // Generate perspective registrations
    var perspectiveRegistrations = _generateRegistrationCode(perspectiveServices, interfaceSnippet, selfSnippet);

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(ServiceRegistrationGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, REGION_NAMESPACE, $"namespace {namespaceName};");
    result = result.Replace("{{PERSPECTIVE_SERVICE_COUNT}}", perspectiveServices.Length.ToString(CultureInfo.InvariantCulture));
    result = result.Replace("{{LENS_SERVICE_COUNT}}", lensServices.Length.ToString(CultureInfo.InvariantCulture));
    result = TemplateUtilities.ReplaceRegion(result, "LENS_REGISTRATIONS", lensRegistrations);
    result = TemplateUtilities.ReplaceRegion(result, "PERSPECTIVE_REGISTRATIONS", perspectiveRegistrations);

    context.AddSource("ServiceRegistrations.g.cs", result);
  }

  /// <summary>
  /// Generates registration code for a set of services.
  /// </summary>
  private static string _generateRegistrationCode(
      ImmutableArray<ServiceRegistrationInfo> services,
      string interfaceSnippet,
      string selfSnippet) {

    if (services.IsEmpty) {
      return string.Empty;
    }

    var sb = new StringBuilder();

    foreach (var service in services) {
      // Register implementation against the user-defined interface
      var interfaceCode = interfaceSnippet
          .Replace(PLACEHOLDER_USER_INTERFACE, service.UserInterfaceName)
          .Replace(PLACEHOLDER_CONCRETE_CLASS, service.ConcreteTypeName);
      sb.AppendLine(TemplateUtilities.IndentCode(interfaceCode, "    "));

      // Conditionally register the concrete type itself (when IncludeSelfRegistration is enabled)
      var selfCode = selfSnippet
          .Replace(PLACEHOLDER_CONCRETE_CLASS, service.ConcreteTypeName);
      sb.AppendLine(TemplateUtilities.IndentCode(selfCode, "    "));
    }

    return sb.ToString();
  }
}
