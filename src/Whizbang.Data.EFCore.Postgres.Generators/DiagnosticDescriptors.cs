using Microsoft.CodeAnalysis;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Diagnostic descriptors for EF Core Postgres generators and analyzers.
/// Uses WHIZ8xx range to avoid conflicts with main Whizbang.Generators (WHIZ001-199).
/// </summary>
internal static class DiagnosticDescriptors {
  private const string CATEGORY = "Whizbang.EFCore";

  /// <summary>
  /// WHIZ810: Warning - Perspective model contains Dictionary property.
  /// EF Core's ComplexProperty().ToJson() does not support Dictionary types.
  /// </summary>
  public static readonly DiagnosticDescriptor PerspectiveModelDictionaryProperty = new(
      id: "WHIZ810",
      title: "Perspective model contains Dictionary property",
      messageFormat: "Property '{0}' on perspective model '{1}' uses Dictionary<{2}, {3}> which is not supported by EF Core's ComplexProperty().ToJson(). Use List<{4}> with Key/Value properties instead.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "EF Core 10's ComplexProperty().ToJson() throws NullReferenceException for Dictionary properties. Use List<T> with Key/Value properties (like ScopeExtension or AttributeEntry pattern) instead."
  );

  /// <summary>
  /// WHIZ070: Error - Vector field requires Pgvector.EntityFrameworkCore package.
  /// </summary>
  /// <docs>diagnostics/WHIZ070</docs>
  /// <tests>VectorFieldPackageReferenceAnalyzerTests.cs:VectorField_MissingPgvectorEFCore_ReportsWHIZ070Async</tests>
  public static readonly DiagnosticDescriptor VectorFieldMissingPgvectorEFCorePackage = new(
      id: "WHIZ070",
      title: "Vector field requires Pgvector.EntityFrameworkCore package",
      messageFormat: "Perspective model uses [VectorField] but Pgvector.EntityFrameworkCore package is not referenced. Add <PackageReference Include=\"Pgvector.EntityFrameworkCore\" /> to your project.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Add <PackageReference Include=\"Pgvector.EntityFrameworkCore\" /> to use vector columns with EF Core. This package provides the UseVector() extension for DbContextOptionsBuilder.",
      customTags: [WellKnownDiagnosticTags.CompilationEnd]
  );

  /// <summary>
  /// WHIZ071: Error - Vector field requires Pgvector package.
  /// </summary>
  /// <docs>diagnostics/WHIZ071</docs>
  /// <tests>VectorFieldPackageReferenceAnalyzerTests.cs:VectorField_MissingPgvector_ReportsWHIZ071Async</tests>
  public static readonly DiagnosticDescriptor VectorFieldMissingPgvectorPackage = new(
      id: "WHIZ071",
      title: "Vector field requires Pgvector package",
      messageFormat: "Perspective model uses [VectorField] but Pgvector package is not referenced. Add <PackageReference Include=\"Pgvector\" /> to your project.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Add <PackageReference Include=\"Pgvector\" /> for NpgsqlDataSourceBuilder.UseVector() support. This is the base package for pgvector types.",
      customTags: [WellKnownDiagnosticTags.CompilationEnd]
  );

  /// <summary>
  /// WHIZ811: Info - Perspective model contains polymorphic type property.
  /// The model uses JSONB storage with System.Text.Json polymorphic serialization.
  /// </summary>
  /// <docs>perspectives/polymorphic-types</docs>
  public static readonly DiagnosticDescriptor PerspectiveModelPolymorphicProperty = new(
      id: "WHIZ811",
      title: "Perspective model contains polymorphic type",
      messageFormat: "Property '{0}' on perspective model '{1}' is abstract type '{2}'. Consider using [PolymorphicDiscriminator] on a discriminator property for efficient type-based queries.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "Abstract types in perspective models are serialized using System.Text.Json polymorphic serialization. For efficient database queries on type discriminators, add a [PolymorphicDiscriminator] attribute to a string property that stores the type name."
  );
}
