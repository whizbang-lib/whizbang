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

  /// <summary>
  /// WHIZ400: Error - Invalid type argument for ILensQuery Query/GetByIdAsync methods.
  /// The type argument must be one of the interface's type parameters.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LensQueryTypeArgumentAnalyzerTests.cs:Query_WithInvalidType_ReportsWHIZ400Async</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LensQueryTypeArgumentAnalyzerTests.cs:GetByIdAsync_WithInvalidType_ReportsWHIZ400Async</tests>
  public static readonly DiagnosticDescriptor InvalidLensQueryTypeArgument = new(
      id: "WHIZ400",
      title: "Invalid type argument for ILensQuery",
      messageFormat: "Type '{0}' is not valid for ILensQuery<{1}>. Valid types are: {2}.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "When using ILensQuery<T1, T2, ...> the type argument to Query<T>() and GetByIdAsync<T>() must be one of the interface's type parameters (T1, T2, etc). Using an unregistered type will cause a runtime ArgumentException."
  );

  /// <summary>
  /// WHIZ401: Warning - Multi-model ILensQuery references unknown perspective model.
  /// </summary>
  /// <docs>diagnostics/WHIZ401</docs>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithMultiModelLensQuery_UnknownModel_ReportsWHIZ401Async</tests>
  public static readonly DiagnosticDescriptor MultiLensQueryUnknownModel = new(
      id: "WHIZ401",
      title: "Multi-model ILensQuery references unknown perspective model",
      messageFormat: "ILensQuery<{0}> in '{1}' references model type '{2}' which is not a known perspective. Ensure an IPerspectiveFor<{2}> exists or register manually.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "All model types in a multi-model ILensQuery must have corresponding perspective implementations for auto-registration."
  );

  /// <summary>
  /// WHIZ402: Info - Multi-model ILensQuery auto-detected.
  /// </summary>
  /// <docs>diagnostics/WHIZ402</docs>
  /// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithMultiModelLensQueryConstructorParam_GeneratesRegistrationAsync</tests>
  public static readonly DiagnosticDescriptor MultiLensQueryDiscovered = new(
      id: "WHIZ402",
      title: "Multi-model ILensQuery auto-detected",
      messageFormat: "Auto-detected ILensQuery<{0}> (arity {1}) in '{2}'. Registration will be generated.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true
  );

  /// <summary>
  /// WHIZ820: Error - Table name exceeds database provider limit.
  /// </summary>
  /// <docs>diagnostics/WHIZ820</docs>
  /// <tests>EFCorePerspectiveConfigurationGeneratorTests.cs:Generator_WithLongTableName_EmitsWHIZ820ErrorAsync</tests>
  public static readonly DiagnosticDescriptor TableNameExceedsLimit = new(
      id: "WHIZ820",
      title: "Table name exceeds database limit",
      messageFormat: "Perspective model '{0}' generates table name '{1}' ({2} bytes) which exceeds {3} limit of {4} bytes. Shorten the model name or configure suffix stripping.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The generated table name exceeds the database provider's maximum identifier length. PostgreSQL allows 63 bytes. Consider shortening the model name, enabling suffix stripping, or using a custom table name."
  );

  /// <summary>
  /// WHIZ821: Error - Column name exceeds database provider limit.
  /// </summary>
  /// <docs>diagnostics/WHIZ821</docs>
  /// <tests>EFCorePerspectiveConfigurationGeneratorTests.cs:Generator_WithLongColumnName_EmitsWHIZ821ErrorAsync</tests>
  public static readonly DiagnosticDescriptor ColumnNameExceedsLimit = new(
      id: "WHIZ821",
      title: "Column name exceeds database limit",
      messageFormat: "Physical field '{0}' on perspective model '{1}' generates column name '{2}' ({3} bytes) which exceeds {4} limit of {5} bytes. Shorten the property name.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The generated column name exceeds the database provider's maximum identifier length. PostgreSQL allows 63 bytes. Consider shortening the property name."
  );

  /// <summary>
  /// WHIZ822: Error - Index name exceeds database provider limit.
  /// </summary>
  /// <docs>diagnostics/WHIZ822</docs>
  /// <tests>EFCorePerspectiveConfigurationGeneratorTests.cs:Generator_WithLongIndexName_EmitsWHIZ822ErrorAsync</tests>
  public static readonly DiagnosticDescriptor IndexNameExceedsLimit = new(
      id: "WHIZ822",
      title: "Index name exceeds database limit",
      messageFormat: "Index '{0}' for physical field '{1}' on perspective model '{2}' ({3} bytes) exceeds {4} limit of {5} bytes. Shorten the table or column name.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The generated index name exceeds the database provider's maximum identifier length. PostgreSQL allows 63 bytes. Index names follow the pattern 'ix_{table}_{column}'. Consider shortening the table or column name."
  );
}
