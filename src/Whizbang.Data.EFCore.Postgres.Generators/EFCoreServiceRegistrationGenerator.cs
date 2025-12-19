using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithWhizbangDbContextAttribute_DiscoversDbContextAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithoutWhizbangDbContextAttribute_DoesNotDiscoverDbContextAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDefaultKey_UsesEmptyStringKeyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithSingleKey_DiscoversDbContextWithKeyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithMultipleKeys_DiscoversDbContextWithAllKeysAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesPartialClassAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesRegistrationMetadataAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesSchemaExtensionsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesOnModelCreatingOverrideAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesOnModelCreatingExtendedHookAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_IncludesRequiredUsingDirectivesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_OnModelCreating_IncludesXmlDocumentationAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithMultipleDbContexts_GeneratesOnModelCreatingForEachAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_SchemaExtensions_IncludesCoreInfrastructureSchemaAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_EventStoreTable_IncludesStreamIdAndScopeColumnsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_SchemaSQL_UsesPropperEscapingForExecuteSqlRawAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_PerspectiveCheckpoints_HasCompositePrimaryKeyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_SchemaExtensions_CallsExecuteMigrationsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithValidDbContext_ProducesNoDiagnosticsAsync</tests>
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
          var dbContexts = data.Left.Right;

          try {
            GenerateRegistrationMetadata(ctx, perspectives!, dbContexts!);
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
  /// Returns null if the class doesn't inherit from DbContext OR doesn't have [WhizbangDbContext] attribute (opt-in required).
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithWhizbangDbContextAttribute_DiscoversDbContextAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithoutWhizbangDbContextAttribute_DoesNotDiscoverDbContextAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDefaultKey_UsesEmptyStringKeyAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithSingleKey_DiscoversDbContextWithKeyAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithMultipleKeys_DiscoversDbContextWithAllKeysAsync</tests>
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
    bool inheritsDbContext = false;
    while (baseType != null) {
      if (baseType.ToDisplayString() == "Microsoft.EntityFrameworkCore.DbContext") {
        inheritsDbContext = true;
        break;
      }
      baseType = baseType.BaseType;
    }

    if (!inheritsDbContext) {
      return null;
    }

    // Check for [WhizbangDbContext] attribute (explicit opt-in required)
    var attribute = symbol.GetAttributes()
        .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Data.EFCore.Custom.WhizbangDbContextAttribute");

    if (attribute is null) {
      return null;  // No attribute = not discovered (opt-in required)
    }

    // Extract keys from attribute
    var keys = ExtractKeysFromAttribute(attribute);
    if (keys.Length == 0) {
      keys = new[] { "" };  // Default to unnamed key
    }

    return new DbContextInfo(
        ClassName: symbol.Name,
        FullyQualifiedName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        Namespace: symbol.ContainingNamespace.ToDisplayString(),
        Keys: keys
    );
  }

  /// <summary>
  /// Extracts perspective information from a class implementing IPerspectiveOf.
  /// Discovers TModel type from IPerspectiveStore&lt;TModel&gt; constructor parameter.
  /// Returns null if the class doesn't implement IPerspectiveOf or doesn't have IPerspectiveStore dependency.
  /// COPIED FROM EFCorePerspectiveConfigurationGenerator to ensure compatibility.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesPartialClassAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesRegistrationMetadataAsync</tests>
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
          var tableName = "wh_per_" + ToSnakeCase(modelType.Name);

          // Check for [WhizbangPerspective] attribute (optional)
          var perspectiveAttribute = symbol.GetAttributes()
              .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Core.Perspectives.WhizbangPerspectiveAttribute");

          string[] keys;
          if (perspectiveAttribute is not null) {
            // Attribute present - extract keys
            keys = ExtractKeysFromAttribute(perspectiveAttribute);
          } else {
            // No attribute - matches default DbContext only
            keys = Array.Empty<string>();
          }

          return new PerspectiveModelInfo(
              PerspectiveClassName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
              ModelTypeName: modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
              TableName: tableName,
              NamespaceHint: symbol.ContainingNamespace.ToDisplayString(),
              Keys: keys
          );
        }
      }
    }

    return null; // No IPerspectiveStore<TModel> found in constructor
  }

  /// <summary>
  /// Converts PascalCase to snake_case.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesPartialClassAsync</tests>
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
  /// Extracts string array keys from attribute constructor arguments.
  /// Supports params string[] parameter pattern used by WhizbangDbContext and WhizbangPerspective attributes.
  /// </summary>
  /// <param name="attribute">The attribute data to extract keys from</param>
  /// <returns>Array of keys, or empty array if no keys found</returns>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDefaultKey_UsesEmptyStringKeyAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithSingleKey_DiscoversDbContextWithKeyAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithMultipleKeys_DiscoversDbContextWithAllKeysAsync</tests>
  private static string[] ExtractKeysFromAttribute(AttributeData attribute) {
    if (attribute.ConstructorArguments.Length == 0) {
      return Array.Empty<string>();
    }

    var arg = attribute.ConstructorArguments[0];

    // Handle params array argument
    if (arg.Kind == TypedConstantKind.Array) {
      return arg.Values
          .Where(v => v.Value is string)
          .Select(v => (string)v.Value!)
          .ToArray();
    }

    return Array.Empty<string>();
  }

  /// <summary>
  /// Determines if a perspective should be included in a DbContext based on key matching.
  /// </summary>
  /// <param name="perspective">The perspective to check</param>
  /// <param name="dbContext">The DbContext to check against</param>
  /// <returns>True if the perspective matches this DbContext's keys</returns>
  /// <remarks>
  /// Matching rules:
  /// - Perspective with no keys (empty array) matches default DbContext only (Key = "")
  /// - Otherwise, any perspective key must match any DbContext key
  /// </remarks>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithSingleKey_DiscoversDbContextWithKeyAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithMultipleKeys_DiscoversDbContextWithAllKeysAsync</tests>
  private static bool MatchesDbContext(PerspectiveModelInfo perspective, DbContextInfo dbContext) {
    // Perspective with no keys = matches default DbContext only
    if (perspective.Keys.Length == 0) {
      return dbContext.Keys.Contains("");
    }

    // Check if any perspective key matches any DbContext key
    return perspective.Keys.Any(pk => dbContext.Keys.Contains(pk));
  }

  /// <summary>
  /// Generates DbContext partial class with DbSet&lt;PerspectiveRow&lt;TModel&gt;&gt; properties.
  /// Loops through each DbContext and generates a separate partial class with matching perspectives.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesPartialClassAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesOnModelCreatingOverrideAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesOnModelCreatingExtendedHookAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_IncludesRequiredUsingDirectivesAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_OnModelCreating_IncludesXmlDocumentationAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithMultipleDbContexts_GeneratesOnModelCreatingForEachAsync</tests>
  private static void GenerateDbContextPartial(
      SourceProductionContext context,
      ImmutableArray<PerspectiveModelInfo> perspectives,
      ImmutableArray<DbContextInfo> dbContexts) {

    // DO NOT return early when perspectives is empty - still need to generate partial class for core entities!
    // Each DbContext needs OnModelCreating() that calls ConfigureWhizbang() for Inbox/Outbox/EventStore

    if (dbContexts.IsEmpty) {
      // Report error - no DbContext found
      var noDbContextDescriptor = new DiagnosticDescriptor(
          id: "EFCORE998",
          title: "No DbContext Found",
          messageFormat: "Could not find any DbContext classes with [WhizbangDbContext] attribute. Partial class generation requires explicit opt-in.",
          category: "Whizbang.Generator",
          defaultSeverity: DiagnosticSeverity.Warning,
          isEnabledByDefault: true);
      context.ReportDiagnostic(Diagnostic.Create(noDbContextDescriptor, Location.None));
      return;
    }

    // Loop through each DbContext and generate partial class
    foreach (var dbContext in dbContexts) {
      // Filter perspectives that match this DbContext's keys
      var matchingPerspectives = perspectives
          .Where(p => MatchesDbContext(p, dbContext))
          .ToList();

      // Report diagnostic if there are no matching perspectives (but still generate partial class for core entities)
      if (matchingPerspectives.Count == 0) {
        var noPerspectivesDescriptor = new DiagnosticDescriptor(
            id: "EFCORE109",
            title: "DbContext Has No Matching Perspectives",
            messageFormat: "DbContext '{0}' with keys [{1}] matched zero perspectives (will still generate partial class for core Whizbang entities)",
            category: "Whizbang.Generator",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true);
        var keysDisplay = string.Join(", ", dbContext.Keys.Select(k => $"\"{k}\""));
        context.ReportDiagnostic(Diagnostic.Create(noPerspectivesDescriptor, Location.None, dbContext.ClassName, keysDisplay));
        // DO NOT CONTINUE - still need to generate partial class with OnModelCreating() for core entities!
      }

      // Collect unique models for this DbContext
      var uniqueModels = matchingPerspectives
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
      sb.AppendLine("using Whizbang.Data.EFCore.Postgres.Generated;");
      sb.AppendLine("using Whizbang.Data.EFCore.Postgres.Configuration;");
      sb.AppendLine();

      sb.AppendLine($"namespace {dbContext.Namespace};");
      sb.AppendLine();

      sb.AppendLine("/// <summary>");
      sb.AppendLine($"/// Auto-generated partial class with DbSet properties for {uniqueModels.Count} perspective model(s).");
      var keysComment = string.Join(", ", dbContext.Keys.Select(k => $"\"{k}\""));
      sb.AppendLine($"/// DbContext keys: [{keysComment}]");
      sb.AppendLine("/// </summary>");
      sb.AppendLine($"public partial class {dbContext.ClassName} {{");

      foreach (var model in uniqueModels) {
        var modelName = ExtractSimpleName(model.ModelTypeName);
        var propertyName = $"{modelName}s";  // Pluralize

        sb.AppendLine($"  /// <summary>");
        sb.AppendLine($"  /// DbSet for {modelName} perspective (table: {model.TableName})");
        sb.AppendLine($"  /// </summary>");
        sb.AppendLine($"  public DbSet<PerspectiveRow<{model.ModelTypeName}>> {propertyName} => Set<PerspectiveRow<{model.ModelTypeName}>>();");
        sb.AppendLine();
      }

      // Generate OnModelCreating override with OnModelCreatingExtended hook
      sb.AppendLine($"  /// <summary>");
      sb.AppendLine($"  /// Configures the EF Core model for this DbContext.");
      sb.AppendLine($"  /// Calls modelBuilder.ConfigureWhizbang() for auto-generated configurations,");
      sb.AppendLine($"  /// then calls OnModelCreatingExtended() for custom user configurations.");
      sb.AppendLine($"  /// </summary>");
      sb.AppendLine($"  protected override void OnModelCreating(ModelBuilder modelBuilder) {{");
      sb.AppendLine($"    // Apply Whizbang-generated configurations");
      sb.AppendLine($"    modelBuilder.ConfigureWhizbang();");
      sb.AppendLine();
      sb.AppendLine($"    // Call user's extended configuration");
      sb.AppendLine($"    OnModelCreatingExtended(modelBuilder);");
      sb.AppendLine($"  }}");
      sb.AppendLine();
      sb.AppendLine($"  /// <summary>");
      sb.AppendLine($"  /// Override this method to extend the model configuration beyond Whizbang's auto-generated setup.");
      sb.AppendLine($"  /// Use this for custom entity configurations, indexes, constraints, etc.");
      sb.AppendLine($"  /// </summary>");
      sb.AppendLine($"  /// <param name=\"modelBuilder\">The builder being used to construct the model for this context.</param>");
      sb.AppendLine($"  partial void OnModelCreatingExtended(ModelBuilder modelBuilder);");

      sb.AppendLine("}");

      context.AddSource($"{dbContext.ClassName}.Generated.g.cs", sb.ToString());

      // Report diagnostic
      var descriptor = new DiagnosticDescriptor(
          id: "EFCORE103",
          title: "DbContext Partial Class Generated",
          messageFormat: "Generated DbContext partial class '{0}' with {1} DbSet properties (keys: [{2}])",
          category: "Whizbang.Generator",
          defaultSeverity: DiagnosticSeverity.Info,
          isEnabledByDefault: true);

      context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, dbContext.ClassName, uniqueModels.Count, keysComment));
    }
  }

  /// <summary>
  /// Generates extension methods that register discovered models directly.
  /// Groups perspectives by DbContext and generates conditional registration per DbContext type.
  /// Uses template snippets for AOT-compatible registration (no reflection).
  /// Always generates infrastructure registration (Inbox/Outbox/EventStore) even when there are no perspectives.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesRegistrationMetadataAsync</tests>
  private static void GenerateRegistrationMetadata(
      SourceProductionContext context,
      ImmutableArray<PerspectiveModelInfo> perspectives,
      ImmutableArray<DbContextInfo> dbContexts) {

    // DEBUG: Always report that we're running
    var debugDescriptor = new DiagnosticDescriptor(
        id: "EFCORE106",
        title: "GenerateRegistrationMetadata Running",
        messageFormat: "GenerateRegistrationMetadata: Found {0} perspectives, {1} DbContexts",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);
    context.ReportDiagnostic(Diagnostic.Create(debugDescriptor, Location.None, perspectives.Length, dbContexts.Length));

    if (dbContexts.IsEmpty) {
      return;  // No DbContext found - nothing to register
    }

    // Group perspectives by DbContext (may be empty if no perspectives)
    var dbContextGroups = dbContexts.Select(dbContext => {
      var matchingPerspectives = perspectives.IsEmpty
          ? new List<PerspectiveModelInfo>()
          : perspectives
              .Where(p => MatchesDbContext(p, dbContext))
              .GroupBy(p => p.ModelTypeName)
              .Select(g => g.First())
              .ToList();
      return (DbContext: dbContext, Models: matchingPerspectives);
    }).ToList();

    // Count total unique models across all DbContexts
    var totalUniqueModels = perspectives.IsEmpty ? 0 : perspectives
        .GroupBy(p => p.ModelTypeName)
        .Count();

    // Load snippets from EFCoreSnippets.cs
    var assembly = typeof(EFCoreServiceRegistrationGenerator).Assembly;
    string infrastructureSnippet;
    string perspectiveSnippet;

    try {
      infrastructureSnippet = TemplateUtilities.ExtractSnippet(
          assembly,
          "EFCoreSnippets.cs",
          "REGISTER_INFRASTRUCTURE_SNIPPET",
          "Whizbang.Data.EFCore.Postgres.Generators.Templates.Snippets"
      );
      perspectiveSnippet = TemplateUtilities.ExtractSnippet(
          assembly,
          "EFCoreSnippets.cs",
          "REGISTER_PERSPECTIVE_MODEL_SNIPPET",
          "Whizbang.Data.EFCore.Postgres.Generators.Templates.Snippets"
      );
    } catch (Exception ex) {
      var errorDescriptor = new DiagnosticDescriptor(
          id: "EFCORE999",
          title: "Failed to Load Snippets",
          messageFormat: "Failed to load snippets from EFCoreSnippets.cs: {0}",
          category: "Whizbang.Generator",
          defaultSeverity: DiagnosticSeverity.Error,
          isEnabledByDefault: true);
      context.ReportDiagnostic(Diagnostic.Create(errorDescriptor, Location.None, ex.Message));
      return;
    }

    var sb = new StringBuilder();

    // File header
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine($"// Generated by Whizbang.Data.EFCore.Postgres.Generators.EFCoreServiceRegistrationGenerator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    sb.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();

    sb.AppendLine("using System.Runtime.CompilerServices;");
    sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    sb.AppendLine("using Whizbang.Core.Lenses;");
    sb.AppendLine("using Whizbang.Data.EFCore.Postgres;");
    sb.AppendLine();

    // Use consumer assembly's namespace to avoid collisions when multiple assemblies reference same generator
    // Each consumer assembly (e.g., ECommerce.BFF.API, ECommerce.InventoryWorker) gets its own .Generated namespace
    var consumerNamespace = dbContextGroups.First().DbContext.Namespace;
    sb.AppendLine($"namespace {consumerNamespace}.Generated;");
    sb.AppendLine();

    sb.AppendLine("/// <summary>");
    sb.AppendLine($"/// Auto-generated module initializer for registering {totalUniqueModels} perspective model(s) across {dbContextGroups.Count} DbContext(s).");
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

    // Generate conditional registration per DbContext
    foreach (var group in dbContextGroups) {
      var keysDisplay = string.Join(", ", group.DbContext.Keys.Select(k => $"\"{k}\""));
      sb.AppendLine($"      // {group.DbContext.ClassName} (keys: [{keysDisplay}])");
      sb.AppendLine($"      if (dbContextType == typeof({group.DbContext.FullyQualifiedName})) {{");

      // Generate infrastructure registration (Inbox, Outbox, EventStore) - once per DbContext
      var infraCode = infrastructureSnippet
          .Replace("__DBCONTEXT_FQN__", group.DbContext.FullyQualifiedName);
      sb.AppendLine(TemplateUtilities.IndentCode(infraCode, "        "));
      sb.AppendLine();

      // Generate perspective model registrations - one per model
      foreach (var model in group.Models) {
        var perspectiveCode = perspectiveSnippet
            .Replace("__MODEL_TYPE__", model.ModelTypeName)
            .Replace("__DBCONTEXT_FQN__", group.DbContext.FullyQualifiedName)
            .Replace("__TABLE_NAME__", model.TableName);
        sb.AppendLine(TemplateUtilities.IndentCode(perspectiveCode, "        "));
        sb.AppendLine();
      }

      sb.AppendLine($"      }}");
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
        messageFormat: "Generated EF Core registration metadata for {0} model type(s) across {1} DbContext(s)",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, totalUniqueModels, dbContextGroups.Count));
  }

  /// <summary>
  /// Generates DbContext schema initialization extensions.
  /// Creates EnsureWhizbangDatabaseInitializedAsync() method for each discovered DbContext.
  /// Core infrastructure schema is generated at runtime by PostgresSchemaBuilder.
  /// Embeds PostgreSQL migration scripts for functions.
  /// Uses template system for code generation.
  /// IMPORTANT: Generates for ALL DbContexts, even those without perspectives.
  /// Inbox/Outbox/EventStore tables still need to be created.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesSchemaExtensionsAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_SchemaExtensions_CallsExecuteMigrationsAsync</tests>
  private static void GenerateSchemaExtensions(
      SourceProductionContext context,
      ImmutableArray<PerspectiveModelInfo> perspectives,
      ImmutableArray<DbContextInfo> dbContexts) {

    if (dbContexts.IsEmpty) {
      return; // No DbContext found
    }

    // Load template once (same template for all DbContexts)
    var assembly = typeof(EFCoreServiceRegistrationGenerator).Assembly;
    var templateBase = TemplateUtilities.GetEmbeddedTemplate(
        assembly,
        "DbContextSchemaExtensionTemplate.cs",
        "Whizbang.Data.EFCore.Postgres.Generators.Templates"
    );

    // Load migration files (once for all DbContexts)
    // Note: Core infrastructure schema is now generated at runtime by PostgresSchemaBuilder
    string migrationsCode = GenerateMigrationsCode(context);

    // Loop through each DbContext and generate extension method
    foreach (var dbContext in dbContexts) {
      // Filter perspectives that match this DbContext's keys (may be empty)
      var matchingPerspectives = perspectives.IsEmpty
          ? new List<PerspectiveModelInfo>()
          : perspectives
              .Where(p => MatchesDbContext(p, dbContext))
              .ToList();

      // Generate perspective tables schema SQL (specific to this DbContext)
      string perspectiveTablesSchema = GeneratePerspectiveTablesSchema(context, matchingPerspectives);

      var template = templateBase;

      // Replace header with timestamp
      template = TemplateUtilities.ReplaceRegion(
          template,
          "HEADER",
          $"// <auto-generated/>\n// Generated by Whizbang.Data.EFCore.Postgres.Generators.EFCoreServiceRegistrationGenerator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n// DO NOT EDIT - Changes will be overwritten\n#nullable enable"
      );

      // Note: CORE_INFRASTRUCTURE_SCHEMA is no longer replaced - template calls PostgresSchemaBuilder at runtime

      // Replace PERSPECTIVE_TABLES_SCHEMA region with embedded perspective tables SQL
      template = TemplateUtilities.ReplaceRegion(
          template,
          "PERSPECTIVE_TABLES_SCHEMA",
          perspectiveTablesSchema
      );

      // Replace MIGRATIONS region with embedded migration scripts
      template = TemplateUtilities.ReplaceRegion(
          template,
          "MIGRATIONS",
          migrationsCode
      );

      // Replace placeholders
      template = template.Replace("__DBCONTEXT_NAMESPACE__", dbContext.Namespace);
      template = template.Replace("__DBCONTEXT_CLASS__", dbContext.ClassName);
      template = template.Replace("__DBCONTEXT_FQN__", dbContext.FullyQualifiedName);

      context.AddSource($"{dbContext.ClassName}_SchemaExtensions.g.cs", template);

      // Report diagnostic
      var descriptor = new DiagnosticDescriptor(
          id: "EFCORE102",
          title: "DbContext Schema Extension Generated",
          messageFormat: "Generated EnsureWhizbangDatabaseInitializedAsync() extension for '{0}' with {1} matching perspective(s)",
          category: "Whizbang.Generator",
          defaultSeverity: DiagnosticSeverity.Info,
          isEnabledByDefault: true);

      context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, dbContext.ClassName, matchingPerspectives.Count));
    }
  }

  /// <summary>
  /// Generates code for migration tuples to embed in generated file.
  /// Reads migration SQL files from generator's own Templates/Migrations (authoritative source).
  /// NOTE: Templates/Migrations must be kept in sync with Whizbang.Data.Postgres/Migrations manually.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_SchemaExtensions_CallsExecuteMigrationsAsync</tests>
  private static string GenerateMigrationsCode(SourceProductionContext context) {
    var sb = new StringBuilder();

    // Load migrations from generator's own embedded resources (Templates/Migrations)
    var assembly = typeof(EFCoreServiceRegistrationGenerator).Assembly;
    var resourcePrefix = $"{assembly.GetName().Name}.Templates.Migrations.";

    // Get all embedded SQL migration files
    var migrationResources = assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(resourcePrefix) && name.EndsWith(".sql"))
        .OrderBy(name => name)
        .ToArray();

    // DIAGNOSTIC: Report what we found (WARNING level to ensure it's visible)
    var diagnosticDescriptor = new DiagnosticDescriptor(
        id: "EFCORE301",
        title: "Migration Files Discovery",
        messageFormat: "Found {0} migration files in {1} (prefix: {2}): {3}",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
    var resourceList = string.Join(", ", migrationResources.Take(5).Select(r => r.Substring(resourcePrefix.Length)));
    if (migrationResources.Length > 5) {
      resourceList += $", ... and {migrationResources.Length - 5} more";
    }
    context.ReportDiagnostic(Diagnostic.Create(diagnosticDescriptor, Location.None,
        migrationResources.Length, assembly.GetName().Name, resourcePrefix, resourceList));

    if (migrationResources.Length == 0) {
      return "// No migration files found in embedded resources";
    }

    // Generate migration tuples
    for (int i = 0; i < migrationResources.Length; i++) {
      var resourceName = migrationResources[i];

      // Extract filename from resource name
      // From "Whizbang.Data.Postgres.Migrations.001_Name.sql" -> "001_Name.sql"
      // Or from "...Templates.Migrations.001_Name.sql" -> "001_Name.sql"
      var fileName = resourceName.Substring(resourcePrefix.Length);

      // Read content from embedded resource
      using var stream = assembly.GetManifestResourceStream(resourceName);
      if (stream == null) {
        continue; // Skip if resource not found
      }

      using var reader = new System.IO.StreamReader(stream);
      var content = reader.ReadToEnd();

      // Escape the SQL content for C# verbatim string literal (@"...")
      // In verbatim strings, only quotes need escaping (by doubling them)
      // IMPORTANT: Also escape curly braces because ExecuteSqlRawAsync treats the string as a format string
      var escapedContent = content
          .Replace("\"", "\"\"")  // Escape quotes for verbatim string
          .Replace("{", "{{")     // Escape opening braces for ExecuteSqlRawAsync
          .Replace("}", "}}");    // Escape closing braces for ExecuteSqlRawAsync

      sb.Append($"      (\"{fileName}\", @\"{escapedContent}\")");

      if (i < migrationResources.Length - 1) {
        sb.AppendLine(",");
      }
    }

    return sb.ToString();
  }

  /// <summary>
  /// Extracts simple type name from fully qualified name.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesPartialClassAsync</tests>
  private static string ExtractSimpleName(string fullyQualifiedName) {
    var withoutGlobal = fullyQualifiedName.Replace("global::", "");
    var lastDot = withoutGlobal.LastIndexOf('.');
    return lastDot >= 0 ? withoutGlobal.Substring(lastDot + 1) : withoutGlobal;
  }


  /// <summary>
  /// Generates CREATE TABLE statements for perspective tables.
  /// Inspects PerspectiveRow&lt;TModel&gt; to determine schema.
  /// PerspectiveRow has fixed schema: id (UUID PK), data (JSONB), metadata (JSONB), scope (JSONB), created_at, updated_at, version (INTEGER).
  /// Returns escaped SQL string ready to embed in C# verbatim string literal.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesSchemaExtensionsAsync</tests>
  private static string GeneratePerspectiveTablesSchema(
    SourceProductionContext context,
    List<PerspectiveModelInfo> perspectives
  ) {
    if (perspectives.Count == 0) {
      return "\"\""; // Empty string - no perspective tables
    }

    var sb = new StringBuilder();
    sb.AppendLine("-- Perspective Tables (auto-generated from PerspectiveRow<TModel> types)");
    sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    sb.AppendLine();

    // Get unique tables (same table might be referenced by multiple perspectives)
    var uniqueTables = perspectives
      .GroupBy(p => p.TableName)
      .Select(g => g.First())
      .OrderBy(p => p.TableName)
      .ToList();

    foreach (var perspective in uniqueTables) {
      // PerspectiveRow<TModel> has fixed schema defined in Whizbang.Core
      sb.AppendLine($"-- {perspective.TableName} (model: {ExtractSimpleName(perspective.ModelTypeName)})");
      sb.AppendLine($"CREATE TABLE IF NOT EXISTS {perspective.TableName} (");
      sb.AppendLine($"  id UUID NOT NULL PRIMARY KEY,");
      sb.AppendLine($"  data JSONB NOT NULL,");
      sb.AppendLine($"  metadata JSONB NOT NULL,");
      sb.AppendLine($"  scope JSONB NOT NULL,");
      sb.AppendLine($"  created_at TIMESTAMPTZ NOT NULL,");
      sb.AppendLine($"  updated_at TIMESTAMPTZ NOT NULL,");
      sb.AppendLine($"  version INTEGER NOT NULL");
      sb.AppendLine($");");
      sb.AppendLine();

      // Add index on created_at for time-based queries (matches EF Core configuration)
      sb.AppendLine($"CREATE INDEX IF NOT EXISTS idx_{perspective.TableName.Replace("wh_per_", "")}_created_at");
      sb.AppendLine($"  ON {perspective.TableName} (created_at);");
      sb.AppendLine();
    }

    // Escape for C# verbatim string
    var sql = sb.ToString()
      .Replace("\"", "\"\"")  // Escape quotes
      .Replace("{", "{{")     // Escape braces for ExecuteSqlRawAsync
      .Replace("}", "}}");

    // Report success
    var descriptor = new DiagnosticDescriptor(
      id: "EFCORE206",
      title: "Perspective Tables Schema Generated",
      messageFormat: "Successfully generated schema for {0} perspective table(s) ({1} characters)",
      category: "Whizbang.Generator",
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true
    );
    context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, uniqueTables.Count, sql.Length));

    return $"@\"{sql}\"";
  }
}

/// <summary>
/// Information about a discovered DbContext class.
/// </summary>
/// <param name="ClassName">Simple class name (e.g., "BffDbContext")</param>
/// <param name="FullyQualifiedName">Fully qualified class name with global:: prefix</param>
/// <param name="Namespace">Containing namespace</param>
/// <param name="Keys">Array of keys that identify which perspectives should be included. Default: [""]</param>
internal sealed record DbContextInfo(
    string ClassName,
    string FullyQualifiedName,
    string Namespace,
    string[] Keys);

/// <summary>
/// Information about a discovered perspective and its TModel type.
/// </summary>
/// <param name="PerspectiveClassName">Fully qualified perspective class name</param>
/// <param name="ModelTypeName">Fully qualified model type name (TModel)</param>
/// <param name="TableName">Snake_case table name</param>
/// <param name="NamespaceHint">Namespace hint for DbContext generation</param>
/// <param name="Keys">Array of keys that identify which DbContexts should include this perspective. Empty = default context only</param>
internal sealed record PerspectiveModelInfo(
    string PerspectiveClassName,
    string ModelTypeName,
    string TableName,
    string NamespaceHint,
    string[] Keys);
