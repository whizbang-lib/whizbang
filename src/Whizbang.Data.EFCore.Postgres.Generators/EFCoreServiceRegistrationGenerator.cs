using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Source generator that discovers perspective model types from DbContext configurations
/// and generates EFCoreRegistrationMetadata for automatic service registration.
/// </summary>
[Generator]
public class EFCoreServiceRegistrationGenerator : IIncrementalGenerator {
  private const string DB_CONTEXT_TYPE = "Microsoft.EntityFrameworkCore.DbContext";
  private const string PERSPECTIVE_ROW_TYPE = "Whizbang.Core.Lenses.PerspectiveRow<TModel>";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover all classes that inherit from DbContext
    var dbContexts = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractDbContextModels(ctx, ct)
    ).Where(static info => info is not null);

    // Generate EFCoreRegistrationMetadata class
    context.RegisterSourceOutput(
        dbContexts.Collect(),
        static (ctx, dbContexts) => GenerateRegistrationMetadata(ctx, dbContexts!)
    );
  }

  /// <summary>
  /// Extracts model types from a DbContext class by analyzing its OnModelCreating method.
  /// </summary>
  private static DbContextModelsInfo? ExtractDbContextModels(
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
      if (baseType.ToDisplayString() == DB_CONTEXT_TYPE) {
        break;
      }
      baseType = baseType.BaseType;
    }

    if (baseType is null) {
      return null;
    }

    // Find OnModelCreating method
    var onModelCreating = symbol.GetMembers("OnModelCreating").OfType<IMethodSymbol>().FirstOrDefault();
    if (onModelCreating is null) {
      return null;
    }

    // Find method syntax
    var methodSyntax = classDecl.Members
        .OfType<MethodDeclarationSyntax>()
        .FirstOrDefault(m => m.Identifier.Text == "OnModelCreating");

    if (methodSyntax is null) {
      return null;
    }

    // Extract model types from method body
    var models = ExtractModelTypesFromMethod(methodSyntax, context.SemanticModel, ct);

    if (models.IsEmpty) {
      return null;
    }

    return new DbContextModelsInfo(models);
  }

  /// <summary>
  /// Extracts model types from OnModelCreating method by analyzing method invocations.
  /// Looks for ConfigurePerspectiveRow&lt;TModel&gt; calls or Entity&lt;PerspectiveRow&lt;TModel&gt;&gt; calls.
  /// </summary>
  private static ImmutableArray<DiscoveredModelInfo> ExtractModelTypesFromMethod(
      MethodDeclarationSyntax method,
      SemanticModel semanticModel,
      CancellationToken ct) {

    var models = ImmutableArray.CreateBuilder<DiscoveredModelInfo>();

    // Find all invocations in the method
    var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();

    foreach (var invocation in invocations) {
      var symbolInfo = semanticModel.GetSymbolInfo(invocation, ct);
      var methodSymbol = symbolInfo.Symbol as IMethodSymbol;

      if (methodSymbol is null) {
        continue;
      }

      // Check for ConfigurePerspectiveRow<TModel>(...)
      if (methodSymbol.Name == "ConfigurePerspectiveRow" && methodSymbol.TypeArguments.Length == 1) {
        var modelType = methodSymbol.TypeArguments[0];
        var tableName = ToSnakeCase(modelType.Name);

        models.Add(new DiscoveredModelInfo(
            TypeName: modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            TableName: tableName
        ));
      }

      // Check for Entity<PerspectiveRow<TModel>>(...)
      if (methodSymbol.Name == "Entity" && methodSymbol.TypeArguments.Length == 1) {
        var entityType = methodSymbol.TypeArguments[0];

        // Check if it's PerspectiveRow<TModel>
        if (entityType is INamedTypeSymbol namedType &&
            namedType.IsGenericType &&
            namedType.ConstructedFrom.ToDisplayString() == PERSPECTIVE_ROW_TYPE) {

          var modelType = namedType.TypeArguments[0];
          var tableName = ToSnakeCase(modelType.Name);

          models.Add(new DiscoveredModelInfo(
              TypeName: modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
              TableName: tableName
          ));
        }
      }
    }

    return models.ToImmutable();
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
  /// Generates the EFCoreRegistrationMetadata class with discovered model types.
  /// </summary>
  private static void GenerateRegistrationMetadata(
      SourceProductionContext context,
      ImmutableArray<DbContextModelsInfo> dbContexts) {

    // Collect all unique models across all DbContexts
    var allModels = ImmutableHashSet.CreateBuilder<DiscoveredModelInfo>();

    foreach (var dbContext in dbContexts) {
      foreach (var model in dbContext.Models) {
        allModels.Add(model);
      }
    }

    if (allModels.Count == 0) {
      // No models found - generate empty metadata
      GenerateEmptyMetadata(context);
      return;
    }

    var sb = new StringBuilder();

    // File header
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine($"// Generated by Whizbang.Data.EFCore.Postgres.Generators.EFCoreServiceRegistrationGenerator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    sb.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();

    sb.AppendLine("using System;");
    sb.AppendLine();

    sb.AppendLine("namespace Whizbang.Data.EFCore.Postgres;");
    sb.AppendLine();

    sb.AppendLine("/// <summary>");
    sb.AppendLine($"/// Metadata for {allModels.Count} discovered perspective model type(s).");
    sb.AppendLine("/// Used by PostgresDriverExtensions for automatic service registration.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("internal static class EFCoreRegistrationMetadata {");
    sb.AppendLine("  internal static readonly ModelTypeInfo[] Models = [");

    foreach (var model in allModels) {
      sb.AppendLine($"    new(typeof({model.TypeName}), \"{model.TableName}\"),");
    }

    sb.AppendLine("  ];");
    sb.AppendLine("}");

    context.AddSource("EFCoreRegistrationMetadata.g.cs", sb.ToString());

    // Report diagnostic
    var descriptor = new DiagnosticDescriptor(
        id: "EFCORE100",
        title: "EF Core Registration Metadata Generated",
        messageFormat: "Generated EF Core registration metadata for {0} model type(s)",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, allModels.Count));
  }

  /// <summary>
  /// Generates empty EFCoreRegistrationMetadata when no models are found.
  /// </summary>
  private static void GenerateEmptyMetadata(SourceProductionContext context) {
    var sb = new StringBuilder();

    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine($"// Generated by Whizbang.Data.EFCore.Postgres.Generators.EFCoreServiceRegistrationGenerator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    sb.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();

    sb.AppendLine("namespace Whizbang.Data.EFCore.Postgres;");
    sb.AppendLine();

    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Empty metadata - no perspective model types discovered.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("internal static class EFCoreRegistrationMetadata {");
    sb.AppendLine("  internal static readonly ModelTypeInfo[] Models = [];");
    sb.AppendLine("}");

    context.AddSource("EFCoreRegistrationMetadata.g.cs", sb.ToString());

    // Report warning
    var descriptor = new DiagnosticDescriptor(
        id: "EFCORE101",
        title: "No Perspective Models Found",
        messageFormat: "No perspective model types found in DbContext configurations. Ensure you call ConfigurePerspectiveRow<TModel> or Entity<PerspectiveRow<TModel>> in OnModelCreating.",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None));
  }
}

/// <summary>
/// Information about models discovered in a DbContext.
/// </summary>
internal sealed record DbContextModelsInfo(ImmutableArray<DiscoveredModelInfo> Models);

/// <summary>
/// Information about a discovered model type.
/// </summary>
internal sealed record DiscoveredModelInfo(string TypeName, string TableName);
