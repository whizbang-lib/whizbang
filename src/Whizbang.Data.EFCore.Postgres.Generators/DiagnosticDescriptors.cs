// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Diagnostic descriptors for EF Core Postgres generators and analyzers.
/// Uses WHIZ8xx range to avoid conflicts with main Whizbang.Generators (WHIZ001-199).
/// </summary>
internal static class DiagnosticDescriptors {
  private const string CATEGORY = "Whizbang.EFCore";
  private const string GENERATOR_CATEGORY = "Whizbang.Generator";

  /// <summary>
  /// WHIZ810: Warning - Perspective model contains Dictionary property.
  /// EF Core's ComplexProperty().ToJson() does not support Dictionary types.
  /// </summary>
  /// <docs>operations/diagnostics/whiz810</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveModelDictionaryAnalyzerTests.cs:PerspectiveModel_WithDictionary_ReportsDiagnosticAsync</tests>
  public static readonly DiagnosticDescriptor PerspectiveModelDictionaryProperty = new(
      id: "WHIZ810",
      title: "Perspective model contains Dictionary property",
      messageFormat: "Property '{0}' on perspective model '{1}' uses Dictionary<{2}, {3}> which is not supported by EF Core's ComplexProperty().ToJson(). Use a List of a class with Key ({2}) and Value ({3}) properties instead.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "EF Core 10's ComplexProperty().ToJson() throws NullReferenceException for Dictionary properties. Use List<T> with Key/Value properties (like ScopeExtension or AttributeEntry pattern) instead."
  );

  /// <summary>
  /// WHIZ812: Warning - a perspective model declares a JSON converter that nothing will consult.
  /// </summary>
  /// <docs>operations/diagnostics/whiz812</docs>
  /// <remarks>
  /// A perspective's document is stored with <c>ComplexProperty().ToJson()</c> and read by Entity
  /// Framework's own materializer, which never consults a <see cref="System.Text.Json.Serialization.JsonConverterAttribute"/>.
  /// Declaring one is therefore silent: no error, no effect, and a value whose stored shape changed stays
  /// unreadable. One consumer declared a converter to keep pre-upgrade rows readable, documented it as the
  /// fix, and it had never run.
  /// <para>
  /// A changed stored shape is what <c>[StoredForm]</c> is for: it rewrites the stored documents in place
  /// at startup, journaled, which also repairs the form for anything reading the column directly.
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Generators.Tests/DeclaredJsonConverterIgnoredTests.cs</tests>
  public static readonly DiagnosticDescriptor PerspectiveModelJsonConverterIgnored = new(
      id: "WHIZ812",
      title: "Perspective model declares a JSON converter that is never consulted",
      messageFormat: "Property '{0}' on perspective model '{1}' declares [JsonConverter(typeof({2}))], which a stored document never consults: the document is read by Entity Framework's JSON materializer, not by the serializer. If the stored shape changed, declare [StoredForm(Previously = typeof(...))] instead, which rewrites the stored documents in place.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "A JsonConverterAttribute on a perspective model property has no effect on the mapped read path. Use [StoredForm] to convert a changed stored shape, or IStoredFormMigration for a conversion it does not cover."
  );

  /// <summary>
  /// WHIZ070: Error - Vector field requires Pgvector.EntityFrameworkCore package.
  /// </summary>
  /// <docs>operations/diagnostics/whiz070</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/VectorFieldPackageReferenceAnalyzerTests.cs:VectorField_MissingPgvectorEFCore_ReportsWHIZ070Async</tests>
  public static readonly DiagnosticDescriptor VectorFieldMissingPgvectorEFCorePackage = new(
      id: "WHIZ070",
      title: "Vector field requires Pgvector.EntityFrameworkCore package",
      messageFormat: "Perspective model uses [VectorField] but Pgvector.EntityFrameworkCore package is not referenced. Add <PackageReference Include=\"Pgvector.EntityFrameworkCore\" /> to your project.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Add <PackageReference Include=\"Pgvector.EntityFrameworkCore\" /> to use vector columns with EF Core. This package provides the UseVector() extension for DbContextOptionsBuilder.",
      customTags: WellKnownDiagnosticTags.CompilationEnd
  );

  /// <summary>
  /// WHIZ071: Error - Vector field requires Pgvector package.
  /// </summary>
  /// <docs>operations/diagnostics/whiz071</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/VectorFieldPackageReferenceAnalyzerTests.cs:VectorField_MissingPgvector_ReportsWHIZ071Async</tests>
  public static readonly DiagnosticDescriptor VectorFieldMissingPgvectorPackage = new(
      id: "WHIZ071",
      title: "Vector field requires Pgvector package",
      messageFormat: "Perspective model uses [VectorField] but Pgvector package is not referenced. Add <PackageReference Include=\"Pgvector\" /> to your project.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Add <PackageReference Include=\"Pgvector\" /> for NpgsqlDataSourceBuilder.UseVector() support. This is the base package for pgvector types.",
      customTags: WellKnownDiagnosticTags.CompilationEnd
  );

  /// <summary>
  /// WHIZ811: Info - Perspective model contains polymorphic type property.
  /// The model uses JSONB storage with System.Text.Json polymorphic serialization.
  /// </summary>
  /// <docs>operations/diagnostics/whiz811</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveModelPolymorphicAnalyzerTests.cs:PerspectiveModel_WithAbstractClassProperty_ReportsWHIZ811Async</tests>
  public static readonly DiagnosticDescriptor PerspectiveModelPolymorphicProperty = new(
      id: "WHIZ811",
      title: "Perspective model contains polymorphic type",
      messageFormat: "Property '{0}' on perspective model '{1}' is polymorphic type '{2}'. Consider adding a [PolymorphicDiscriminator] property to '{1}' for efficient type-based queries.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "Abstract and [JsonPolymorphic] types in perspective models are serialized using System.Text.Json polymorphic serialization. For efficient database queries on type discriminators, add a [PolymorphicDiscriminator] attribute to a string property that stores the type name."
  );

  /// <summary>
  /// WHIZ400: Error - Invalid type argument for ILensQuery Query/GetByIdAsync methods.
  /// The type argument must be one of the interface's type parameters.
  /// </summary>
  /// <docs>operations/diagnostics/whiz400</docs>
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
  /// <docs>operations/diagnostics/whiz401</docs>
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
  /// <docs>operations/diagnostics/whiz402</docs>
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
  /// <docs>operations/diagnostics/whiz820</docs>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:Generator_WithLongTableName_EmitsWHIZ820ErrorAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveTableNamingDiagnosticsTests.cs:EFCoreGenerator_WithAManualNameOverTheLimit_ReportsWHIZ820NamingTheAttributeAsync</tests>
  public static readonly DiagnosticDescriptor TableNameExceedsLimit = new(
      id: "WHIZ820",
      title: "Table name exceeds database limit",
      messageFormat: "Perspective model '{0}' generates table name '{1}' ({2} bytes) which exceeds {3} limit of {4} bytes. Shorten the model name, configure suffix stripping, or name the table with [PerspectiveTable].",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The table name, generated or set with [PerspectiveTable], exceeds the database provider's maximum identifier length. PostgreSQL allows 63 bytes. Shorten the model name, enable suffix stripping, or name the table with [PerspectiveTable(\"wh_per_...\")]."
  );

  /// <summary>
  /// WHIZ821: Error - Column name exceeds database provider limit.
  /// </summary>
  /// <docs>operations/diagnostics/whiz821</docs>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:Generator_WithLongColumnName_EmitsWHIZ821ErrorAsync</tests>
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
  /// <docs>operations/diagnostics/whiz822</docs>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:Generator_WithLongIndexName_EmitsWHIZ822ErrorAsync</tests>
  public static readonly DiagnosticDescriptor IndexNameExceedsLimit = new(
      id: "WHIZ822",
      title: "Index name exceeds database limit",
      messageFormat: "Index '{0}' for physical field '{1}' on perspective model '{2}' ({3} bytes) exceeds {4} limit of {5} bytes. Shorten the table or column name.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The generated index name exceeds the database provider's maximum identifier length. PostgreSQL allows 63 bytes. Index names follow the pattern 'ix_{table}_{column}'. Consider shortening the table or column name."
  );

  /// <summary>
  /// WHIZ830: Error - A stored-form declaration the generator cannot turn into SQL.
  /// </summary>
  /// <docs>operations/diagnostics/whiz830</docs>
  /// <tests>tests/Whizbang.Generators.Tests/StoredFormMigrationGenerationTests.cs:ADeclarationItCannotGenerate_IsWHIZ830_AndEmitsNothingAsync</tests>
  public static readonly DiagnosticDescriptor StoredFormCannotBeGenerated = new(
      id: "WHIZ830",
      title: "Stored-form migration cannot be generated",
      messageFormat: "The stored-form declaration on {0} cannot be generated: {1}. Write an IStoredFormMigration for it.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "A [StoredForm] or [StoredFormRemoved] declaration has no generated conversion. Nothing is emitted for it, so stored documents would stay in the old form; a custom IStoredFormMigration covers any change."
  );

  /// <summary>
  /// WHIZ831: Warning - A custom stored-form migration that never runs.
  /// </summary>
  /// <docs>operations/diagnostics/whiz831</docs>
  /// <tests>tests/Whizbang.Generators.Tests/StoredFormMigrationGenerationTests.cs:ADeclarationInsideACollectionElement_IsWHIZ832_AndAnOrphanMigrationWHIZ831Async</tests>
  public static readonly DiagnosticDescriptor StoredFormMigrationNeverRuns = new(
      id: "WHIZ831",
      title: "Stored-form migration never runs",
      messageFormat: "{0} is a stored-form migration that never runs: {1}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "An IStoredFormMigration<TModel> runs against the table of the perspective whose model is TModel, and is created with its parameterless constructor. Without either it is never run."
  );

  /// <summary>
  /// WHIZ832: Warning - A stored-form declaration inside an element of a collection.
  /// </summary>
  /// <docs>operations/diagnostics/whiz832</docs>
  /// <tests>tests/Whizbang.Generators.Tests/StoredFormMigrationGenerationTests.cs:ADeclarationInsideACollectionElement_IsWHIZ832_AndAnOrphanMigrationWHIZ831Async</tests>
  public static readonly DiagnosticDescriptor StoredFormInsideCollection = new(
      id: "WHIZ832",
      title: "Stored-form declaration inside a collection element",
      messageFormat: "The stored-form declaration on {0} is not generated: {1}. Write an IStoredFormMigration for it.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "A declaration on a property of a collection's element type has no single document path a generated statement could convert."
  );

  /// <summary>
  /// WHIZ833: Warning - Two custom stored-form migrations of one table state the same order.
  /// </summary>
  /// <docs>operations/diagnostics/whiz833</docs>
  /// <tests>tests/Whizbang.Generators.Tests/StoredFormMigrationGenerationTests.cs:TwoMigrationsOfOneTableSharingAnOrder_AreWHIZ833_OncePerSharedOrderAsync</tests>
  public static readonly DiagnosticDescriptor StoredFormMigrationOrderShared = new(
      id: "WHIZ833",
      title: "Stored-form migrations share an order",
      messageFormat: "The stored-form migrations {0} of {1} share Order {2}, so they run in class-name order. Give each its own Order.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Custom stored-form migrations of one table run in the Order their authors state. Two with the same Order still run in a stable order, their classes' full names, but that order is an accident of naming rather than a decision; renaming a class would change it."
  );
  /// <summary>
  /// WHIZ701: Info - perspectives discovered for the EF Core model configuration.
  /// </summary>
  /// <docs>operations/diagnostics/whiz701</docs>
  /// <tests>tests/Whizbang.Generators.Tests/CompilationNoticeDiagnosticTests.cs:Whiz701_ReportsThePerspectiveCountAsync</tests>
  public static readonly DiagnosticDescriptor EFCorePerspectiveDiscovery = new(
      id: "WHIZ701",
      title: "EF Core Perspective Discovery",
      messageFormat: "Discovered {0} perspective(s) for EF Core ModelBuilder configuration",
      category: GENERATOR_CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "Reports how many perspectives the generator configures in the EF Core model. It is reported without a location because it is about the whole compilation, not one declaration; turn it off for a project in .globalconfig or NoWarn.");

  /// <summary>
  /// WHIZ702: Info - the embedded migration files the generated DbContext will apply.
  /// </summary>
  /// <docs>operations/diagnostics/whiz702</docs>
  /// <tests>tests/Whizbang.Generators.Tests/CompilationNoticeDiagnosticTests.cs:Whiz702_ReportsTheMigrationFilesFoundAsync</tests>
  public static readonly DiagnosticDescriptor EFCoreMigrationFilesDiscovery = new(
      id: "WHIZ702",
      title: "EF Core Migration Files Discovery",
      messageFormat: "Found {0} migration files in {1} (prefix: {2}): {3}",
      category: GENERATOR_CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "Reports the embedded migration files the generated DbContext applies at startup. It is reported without a location because it is about the whole compilation, not one declaration; turn it off for a project in .globalconfig or NoWarn.");

  /// <summary>
  /// WHIZ703: Info - no DbContext opted in with [WhizbangDbContext], so no partial class is generated.
  /// </summary>
  /// <docs>operations/diagnostics/whiz703</docs>
  /// <tests>tests/Whizbang.Generators.Tests/CompilationNoticeDiagnosticTests.cs:Whiz703_ReportsThatNoDbContextWasFoundAsync</tests>
  public static readonly DiagnosticDescriptor NoWhizbangDbContextFound = new(
      id: "WHIZ703",
      title: "No DbContext Found",
      messageFormat: "Could not find any DbContext classes with [WhizbangDbContext] attribute. Partial class generation requires explicit opt-in.",
      category: GENERATOR_CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "No DbContext carries [WhizbangDbContext], which is expected in a library project; partial class generation requires that opt-in. It is reported without a location because it is about the whole compilation, not one declaration; turn it off for a project in .globalconfig or NoWarn.");
}
