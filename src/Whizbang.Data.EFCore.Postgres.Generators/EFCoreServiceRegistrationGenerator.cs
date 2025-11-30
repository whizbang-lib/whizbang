using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Source generator that discovers IPerspectiveOf&lt;TEvent&gt; implementations,
/// extracts their TModel types, and generates:
/// 1. DbContext partial class with DbSet&lt;PerspectiveRow&lt;TModel&gt;&gt; properties
/// 2. EFCoreRegistrationMetadata for automatic service registration
/// 3. EnsureWhizbangTablesCreatedAsync() extension method for schema creation
/// </summary>
[Generator]
public class EFCoreServiceRegistrationGenerator : IIncrementalGenerator {
  private const string PERSPECTIVE_ROW_TYPE = "Whizbang.Core.Lenses.PerspectiveRow<TModel>";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Generate marker file to confirm generator is running
    context.RegisterPostInitializationOutput(ctx => {
      ctx.AddSource("_EFCoreGenerator_Initialized.g.cs",
        $"// EFCoreServiceRegistrationGenerator initialized at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n" +
        $"// Looking for IPerspectiveOf<TEvent> implementations with IPerspectiveStore<TModel> constructor parameters");
    });

    // Discover all perspective classes that implement IPerspectiveOf<TEvent>
    var perspectives = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractPerspectiveInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Discover DbContext classes
    var dbContextClasses = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractDbContextInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Combine perspectives with DbContext info and compilation
    var allData = perspectives.Collect()
        .Combine(dbContextClasses.Collect())
        .Combine(context.CompilationProvider);

    // Generate DbContext partial class with DbSet<PerspectiveRow<TModel>> properties
    context.RegisterSourceOutput(
        allData,
        static (ctx, data) => {
          var perspectives = data.Left.Left;
          var dbContexts = data.Left.Right;
          var compilation = data.Right;

          try {
            // Always report count, even if zero (for debugging)
            var descriptor = new DiagnosticDescriptor(
                id: "EFCORE104",
                title: "EFCore Generator Running",
                messageFormat: "EFCoreServiceRegistrationGenerator found {0} perspective(s) with models",
                category: "Whizbang.Generator",
                defaultSeverity: DiagnosticSeverity.Info,
                isEnabledByDefault: true);
            ctx.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, perspectives.Length));

            // Report each discovered perspective
            foreach (var perspective in perspectives) {
              var modelDescriptor = new DiagnosticDescriptor(
                  id: "EFCORE105",
                  title: "Perspective Model Discovered",
                  messageFormat: "Found perspective {0} with model {1} (table: {2})",
                  category: "Whizbang.Generator",
                  defaultSeverity: DiagnosticSeverity.Info,
                  isEnabledByDefault: true);
              ctx.ReportDiagnostic(Diagnostic.Create(modelDescriptor, Location.None,
                  perspective.PerspectiveClassName,
                  perspective.ModelTypeName,
                  perspective.TableName));
            }

            GenerateDbContextPartial(ctx, perspectives!, dbContexts!);
          } catch (Exception ex) {
            var descriptor = new DiagnosticDescriptor(
                id: "EFCORE997",
                title: "EFCore Generator Error",
                messageFormat: "Error in GenerateDbContextPartial: {0}",
                category: "Whizbang.Generator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);
            ctx.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, ex.Message));
          }
        }
    );

    // Generate EFCoreRegistrationMetadata class
    context.RegisterSourceOutput(
        allData,
        static (ctx, data) => {
          var perspectives = data.Left.Left;

          try {
            GenerateRegistrationMetadata(ctx, perspectives!);
          } catch (Exception ex) {
            var descriptor = new DiagnosticDescriptor(
                id: "EFCORE996",
                title: "EFCore Generator Error",
                messageFormat: "Error in GenerateRegistrationMetadata: {0}",
                category: "Whizbang.Generator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);
            ctx.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, ex.Message));
          }
        }
    );

    // Generate DbContext schema extensions (EnsureWhizbangTablesCreatedAsync)
    context.RegisterSourceOutput(
        allData,
        static (ctx, data) => {
          var perspectives = data.Left.Left;
          var dbContexts = data.Left.Right;

          try {
            GenerateSchemaExtensions(ctx, perspectives!, dbContexts!);
          } catch (Exception ex) {
            var descriptor = new DiagnosticDescriptor(
                id: "EFCORE995",
                title: "EFCore Generator Error",
                messageFormat: "Error in GenerateSchemaExtensions: {0}",
                category: "Whizbang.Generator",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);
            ctx.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, ex.Message));
          }
        }
    );
  }

  /// <summary>
  /// Extracts DbContext information from a class inheriting from DbContext.
  /// Returns null if the class doesn't inherit from DbContext.
  /// </summary>
  private static DbContextInfo? ExtractDbContextInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var classDecl = (ClassDeclarationSyntax)context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;

    if (symbol is null) {
      return null;
    }

    // Check if class inherits from DbContext
    var baseType = symbol.BaseType;
    while (baseType != null) {
      if (baseType.ToDisplayString() == "Microsoft.EntityFrameworkCore.DbContext") {
        return new DbContextInfo(
            ClassName: symbol.Name,
            FullyQualifiedName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Namespace: symbol.ContainingNamespace.ToDisplayString()
        );
      }
      baseType = baseType.BaseType;
    }

    return null; // Doesn't inherit from DbContext
  }

  /// <summary>
  /// Extracts perspective information from a class implementing IPerspectiveOf.
  /// Discovers TModel type from IPerspectiveStore&lt;TModel&gt; constructor parameter.
  /// Returns null if the class doesn't implement IPerspectiveOf or doesn't have IPerspectiveStore dependency.
  /// COPIED FROM EFCorePerspectiveConfigurationGenerator to ensure compatibility.
  /// </summary>
  private static PerspectiveModelInfo? ExtractPerspectiveInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var classDecl = (ClassDeclarationSyntax)context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;

    if (symbol is null) {
      return null;
    }

    // Check if class implements IPerspectiveOf<TEvent>
    // Note: IPerspectiveOf is generic with ONE type parameter (TEvent)
    bool implementsIPerspectiveOf = symbol.AllInterfaces.Any(i => {
      var originalDef = i.OriginalDefinition.ToDisplayString();
      // IPerspectiveOf<TEvent> has full name "Whizbang.Core.IPerspectiveOf<TEvent>"
      return originalDef.StartsWith("Whizbang.Core.IPerspectiveOf<");
    });

    if (!implementsIPerspectiveOf) {
      return null;
    }

    // Find IPerspectiveStore<TModel> in constructor parameters
    var constructor = symbol.Constructors.FirstOrDefault();
    if (constructor is null) {
      return null;
    }

    foreach (var parameter in constructor.Parameters) {
      if (parameter.Type is INamedTypeSymbol parameterType) {
        var originalDef = parameterType.OriginalDefinition.ToDisplayString();

        // IPerspectiveStore<TModel> has full name "Whizbang.Core.Perspectives.IPerspectiveStore<TModel>"
        if (originalDef.StartsWith("Whizbang.Core.Perspectives.IPerspectiveStore<")) {
          // Get TModel from IPerspectiveStore<TModel>
          var modelType = parameterType.TypeArguments[0];
          var tableName = ToSnakeCase(modelType.Name);

          return new PerspectiveModelInfo(
              PerspectiveClassName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
              ModelTypeName: modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
              TableName: tableName,
              NamespaceHint: symbol.ContainingNamespace.ToDisplayString()
          );
        }
      }
    }

    return null; // No IPerspectiveStore<TModel> found in constructor
  }

  /// <summary>
  /// Converts PascalCase to snake_case.
  /// </summary>
  private static string ToSnakeCase(string input) {
    if (string.IsNullOrEmpty(input)) {
      return input;
    }

    var sb = new StringBuilder();
    sb.Append(char.ToLowerInvariant(input[0]));

    for (int i = 1; i < input.Length; i++) {
      char c = input[i];
      if (char.IsUpper(c)) {
        sb.Append('_');
        sb.Append(char.ToLowerInvariant(c));
      } else {
        sb.Append(c);
      }
    }

    return sb.ToString();
  }

  /// <summary>
  /// Generates DbContext partial class with DbSet&lt;PerspectiveRow&lt;TModel&gt;&gt; properties.
  /// </summary>
  private static void GenerateDbContextPartial(
      SourceProductionContext context,
      ImmutableArray<PerspectiveModelInfo> perspectives,
      ImmutableArray<DbContextInfo> dbContexts) {

    if (perspectives.IsEmpty) {
      return;  // No perspectives found
    }

    if (dbContexts.IsEmpty) {
      // Report error - no DbContext found
      var noDbContextDescriptor = new DiagnosticDescriptor(
          id: "EFCORE998",
          title: "No DbContext Found",
          messageFormat: "Could not find any DbContext classes in the compilation. Partial class generation requires a DbContext.",
          category: "Whizbang.Generator",
          defaultSeverity: DiagnosticSeverity.Warning,
          isEnabledByDefault: true);
      context.ReportDiagnostic(Diagnostic.Create(noDbContextDescriptor, Location.None));
      return;
    }

    // Use the first DbContext found (there should typically only be one per project)
    var dbContext = dbContexts[0];
    var dbContextNamespace = dbContext.Namespace;
    var dbContextClassName = dbContext.ClassName;

    // Collect unique models
    var uniqueModels = perspectives
        .GroupBy(p => p.ModelTypeName)
        .Select(g => g.First())
        .ToList();

    var sb = new StringBuilder();

    // File header
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine($"// Generated by Whizbang.Data.EFCore.Postgres.Generators.EFCoreServiceRegistrationGenerator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    sb.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();

    sb.AppendLine("using Microsoft.EntityFrameworkCore;");
    sb.AppendLine("using Whizbang.Core.Lenses;");
    sb.AppendLine();

    sb.AppendLine($"namespace {dbContextNamespace};");
    sb.AppendLine();

    sb.AppendLine("/// <summary>");
    sb.AppendLine($"/// Auto-generated partial class with DbSet properties for {uniqueModels.Count} perspective model(s).");
    sb.AppendLine("/// </summary>");
    sb.AppendLine($"public partial class {dbContextClassName} {{");

    foreach (var model in uniqueModels) {
      var modelName = ExtractSimpleName(model.ModelTypeName);
      var propertyName = $"{modelName}s";  // Pluralize

      sb.AppendLine($"  /// <summary>");
      sb.AppendLine($"  /// DbSet for {modelName} perspective (table: {model.TableName})");
      sb.AppendLine($"  /// </summary>");
      sb.AppendLine($"  public DbSet<PerspectiveRow<{model.ModelTypeName}>> {propertyName} => Set<PerspectiveRow<{model.ModelTypeName}>>();");
      sb.AppendLine();
    }

    sb.AppendLine("}");

    context.AddSource($"{dbContextClassName}.Generated.g.cs", sb.ToString());

    // Report diagnostic
    var descriptor = new DiagnosticDescriptor(
        id: "EFCORE103",
        title: "DbContext Partial Class Generated",
        messageFormat: "Generated DbContext partial class with {0} DbSet properties",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, uniqueModels.Count));
  }

  /// <summary>
  /// Generates extension methods that register discovered models directly.
  /// This is generated in the consumer project and calls library methods with the discovered models.
  /// </summary>
  private static void GenerateRegistrationMetadata(
      SourceProductionContext context,
      ImmutableArray<PerspectiveModelInfo> perspectives) {

    if (perspectives.IsEmpty) {
      return;  // No perspectives found
    }

    // Collect unique models
    var uniqueModels = perspectives
        .GroupBy(p => p.ModelTypeName)
        .Select(g => g.First())
        .ToList();

    var sb = new StringBuilder();

    // File header
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine($"// Generated by Whizbang.Data.EFCore.Postgres.Generators.EFCoreServiceRegistrationGenerator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    sb.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();

    sb.AppendLine("using System.Runtime.CompilerServices;");
    sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    sb.AppendLine("using Whizbang.Data.EFCore.Postgres;");
    sb.AppendLine();

    sb.AppendLine("namespace Whizbang.Data.EFCore.Postgres.Generated;");
    sb.AppendLine();

    sb.AppendLine("/// <summary>");
    sb.AppendLine($"/// Auto-generated module initializer for registering {uniqueModels.Count} discovered perspective model(s).");
    sb.AppendLine("/// Runs at module load time and registers models with ModelRegistrationRegistry (AOT-compatible).");
    sb.AppendLine("/// For test assemblies where ModuleInitializers may not run reliably, call Initialize() explicitly.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("public static class GeneratedModelRegistration {");
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Module initializer that registers the model registration callback.");
    sb.AppendLine("  /// This runs automatically when the assembly is loaded (no reflection required).");
    sb.AppendLine("  /// For test assemblies, you can call this method explicitly in test setup.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  [ModuleInitializer]");
    sb.AppendLine("  public static void Initialize() {");
    sb.AppendLine("    // Register callback with the library's registry");
    sb.AppendLine("    ModelRegistrationRegistry.RegisterModels((services, dbContextType, upsertStrategy) => {");
    sb.AppendLine();

    foreach (var model in uniqueModels) {
      sb.AppendLine($"      // Register {model.ModelTypeName}");
      sb.AppendLine($"      EFCoreInfrastructureRegistration.RegisterPerspectiveModel(");
      sb.AppendLine($"          services,");
      sb.AppendLine($"          dbContextType,");
      sb.AppendLine($"          typeof({model.ModelTypeName}),");
      sb.AppendLine($"          \"{model.TableName}\",");
      sb.AppendLine($"          upsertStrategy);");
      sb.AppendLine();
    }

    sb.AppendLine("    });");
    sb.AppendLine("  }");
    sb.AppendLine("}");

    context.AddSource("EFCoreModelRegistration.g.cs", sb.ToString());

    // Report diagnostic
    var descriptor = new DiagnosticDescriptor(
        id: "EFCORE100",
        title: "EF Core Registration Metadata Generated",
        messageFormat: "Generated EF Core registration metadata for {0} model type(s)",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, uniqueModels.Count));
  }

  /// <summary>
  /// Generates DbContext schema initialization extensions.
  /// Creates EnsureWhizbangTablesCreatedAsync() method for the discovered DbContext.
  /// Uses template system for code generation.
  /// </summary>
  private static void GenerateSchemaExtensions(
      SourceProductionContext context,
      ImmutableArray<PerspectiveModelInfo> perspectives,
      ImmutableArray<DbContextInfo> dbContexts) {

    if (perspectives.IsEmpty) {
      return; // No perspectives found
    }

    if (dbContexts.IsEmpty) {
      return; // No DbContext found
    }

    // Use the first DbContext found
    var dbContext = dbContexts[0];
    var dbContextNamespace = dbContext.Namespace;
    var dbContextClassName = dbContext.ClassName;
    var dbContextFQN = dbContext.FullyQualifiedName;

    // Load template
    var assembly = typeof(EFCoreServiceRegistrationGenerator).Assembly;
    var templateBase = TemplateUtilities.GetEmbeddedTemplate(
        assembly,
        "DbContextSchemaExtensionTemplate.cs",
        "Whizbang.Data.EFCore.Postgres.Generators.Templates"
    );

    var template = templateBase;

    // Replace header with timestamp
    template = TemplateUtilities.ReplaceRegion(
        template,
        "HEADER",
        $"// <auto-generated/>\n// Generated by Whizbang.Data.EFCore.Postgres.Generators.EFCoreServiceRegistrationGenerator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n// DO NOT EDIT - Changes will be overwritten\n#nullable enable"
    );

    // Replace placeholders
    template = template.Replace("__DBCONTEXT_NAMESPACE__", dbContextNamespace);
    template = template.Replace("__DBCONTEXT_CLASS__", dbContextClassName);
    template = template.Replace("__DBCONTEXT_FQN__", dbContextFQN);

    context.AddSource($"{dbContextClassName}_SchemaExtensions.g.cs", template);

    // Report diagnostic
    var descriptor = new DiagnosticDescriptor(
        id: "EFCORE102",
        title: "DbContext Schema Extension Generated",
        messageFormat: "Generated EnsureWhizbangTablesCreatedAsync() extension for {0}",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, dbContextClassName));
  }

  /// <summary>
  /// Extracts simple type name from fully qualified name.
  /// </summary>
  private static string ExtractSimpleName(string fullyQualifiedName) {
    var withoutGlobal = fullyQualifiedName.Replace("global::", "");
    var lastDot = withoutGlobal.LastIndexOf('.');
    return lastDot >= 0 ? withoutGlobal.Substring(lastDot + 1) : withoutGlobal;
  }
}

/// <summary>
/// Information about a discovered DbContext class.
/// </summary>
/// <param name="ClassName">Simple class name (e.g., "BffDbContext")</param>
/// <param name="FullyQualifiedName">Fully qualified class name with global:: prefix</param>
/// <param name="Namespace">Containing namespace</param>
internal sealed record DbContextInfo(
    string ClassName,
    string FullyQualifiedName,
    string Namespace);

/// <summary>
/// Information about a discovered perspective and its TModel type.
/// </summary>
/// <param name="PerspectiveClassName">Fully qualified perspective class name</param>
/// <param name="ModelTypeName">Fully qualified model type name (TModel)</param>
/// <param name="TableName">Snake_case table name</param>
/// <param name="NamespaceHint">Namespace hint for DbContext generation</param>
internal sealed record PerspectiveModelInfo(
    string PerspectiveClassName,
    string ModelTypeName,
    string TableName,
    string NamespaceHint);
