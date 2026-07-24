using System.Collections.Immutable;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedCode_ImplementsIDiagnosticsInterfaceAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_HasCorrectGeneratorNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_ReportsCorrectPerspectiveCountAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:LogDiscoveryDiagnostics_OutputsPerspectiveDetailsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_WithNoPerspectives_ReportsZeroAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_DeduplicatesPerspectivesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithWhizbangDbContextAttribute_DiscoversDbContextAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesPartialClassAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesRegistrationMetadataAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_WithDiscoveredDbContext_GeneratesSchemaExtensionsAsync</tests>
/// Value type containing information about a discovered perspective.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="ModelTypeName">Fully qualified model type name (e.g., "global::MyApp.Orders.OrderSummary")</param>
/// <param name="TableName">PostgreSQL table name for this perspective</param>
/// <param name="PhysicalFields">Array of physical fields discovered on the model</param>
/// <param name="HasPolymorphicProperties">Whether the model contains abstract/polymorphic type properties</param>
internal sealed record PerspectiveInfo(
    string ModelTypeName,
    string TableName,
    ImmutableArray<PhysicalFieldInfo> PhysicalFields,
    bool HasPolymorphicProperties,
    bool IsSplitMode = false
);

/// <summary>
/// Intermediate value type for perspective discovery before table name config is applied.
/// Separates syntax/semantic extraction from configuration-dependent table name generation.
/// </summary>
/// <param name="ModelTypeName">Fully qualified model type name (e.g., "global::MyApp.Orders.OrderSummary")</param>
/// <param name="TableBaseName">Base name for table generation (before suffix stripping and prefix)</param>
/// <param name="PhysicalFields">Array of physical fields discovered on the model</param>
/// <param name="HasPolymorphicProperties">Whether the model contains abstract/polymorphic type properties</param>
/// <param name="IsSplitMode">Whether the model uses FieldStorageMode.Split</param>
internal sealed record PerspectiveCandidate(
    string ModelTypeName,
    string TableBaseName,
    ImmutableArray<PhysicalFieldInfo> PhysicalFields,
    bool HasPolymorphicProperties,
    bool IsSplitMode = false
);
