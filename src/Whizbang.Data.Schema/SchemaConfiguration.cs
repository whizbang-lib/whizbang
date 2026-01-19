namespace Whizbang.Data.Schema;

/// <summary>
/// <docs>extensibility/database-schema-framework</docs>
/// <tests>tests/Whizbang.Data.Schema.Tests/SchemaConfigurationTests.cs:SchemaConfiguration_WithoutParameters_UsesDefaultsAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SchemaConfigurationTests.cs:SchemaConfiguration_WithCustomInfrastructurePrefix_SetsValueAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SchemaConfigurationTests.cs:SchemaConfiguration_WithCustomPerspectivePrefix_SetsValueAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SchemaConfigurationTests.cs:SchemaConfiguration_WithCustomSchemaName_SetsValueAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SchemaConfigurationTests.cs:SchemaConfiguration_WithCustomVersion_SetsValueAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SchemaConfigurationTests.cs:SchemaConfiguration_WithAllCustom_SetsAllAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SchemaConfigurationTests.cs:SchemaConfiguration_SameValues_AreEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SchemaConfigurationTests.cs:SchemaConfiguration_DifferentPrefix_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SchemaConfigurationTests.cs:SchemaConfiguration_IsRecordAsync</tests>
/// Schema configuration with dual-prefix system.
/// Infrastructure tables: wh_inbox, wh_outbox, etc.
/// Perspective tables: wh_per_product_dto, wh_per_order_summary, etc.
/// Uses record with structural equality (critical for incremental generators).
/// </summary>
/// <param name="InfrastructurePrefix">Prefix for infrastructure tables (default: "wh_")</param>
/// <param name="PerspectivePrefix">Prefix for perspective tables (default: "wh_per_")</param>
/// <param name="SchemaName">Database schema name (default: "public")</param>
/// <param name="Version">Schema version for migrations (default: 1)</param>
public sealed record SchemaConfiguration(
  string InfrastructurePrefix = "wh_",
  string PerspectivePrefix = "wh_per_",
  string SchemaName = "public",
  int Version = 1
);
