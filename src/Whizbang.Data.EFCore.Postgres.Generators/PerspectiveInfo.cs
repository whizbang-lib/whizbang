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
internal sealed record PerspectiveInfo(
    string ModelTypeName,
    string TableName
);
