namespace Whizbang.Core.Messaging;

/// <summary>
/// Permanent deduplication tracking for idempotent delivery guarantees.
/// Records are never deleted - this table grows forever.
/// Maps to wh_message_deduplication table.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaDefinitionTests.cs:CoreInfrastructureSchema_ShouldCreateAllRequiredTablesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaDefinitionTests.cs:PostgresSchemaBuilder_ShouldGenerateValidSQLAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationTests.cs:EnsureWhizbangDatabaseInitialized_CreatesCoreInfrastructureTablesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs:Generator_SchemaExtensions_IncludesCoreInfrastructureSchemaAsync</tests>
public class MessageDeduplicationRecord {
  /// <summary>
  /// Message ID (UUIDv7).
  /// Primary key.
  /// </summary>
  public required Guid MessageId { get; set; }

  /// <summary>
  /// UTC timestamp when this message was first received.
  /// </summary>
  public required DateTimeOffset FirstSeenAt { get; set; }
}
