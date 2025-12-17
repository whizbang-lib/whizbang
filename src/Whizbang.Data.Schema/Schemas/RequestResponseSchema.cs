using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the request_response table (request/response tracking).
/// Table name: {prefix}request_response (e.g., wb_request_response)
/// Stores request/response pairs for async communication patterns.
/// </summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/RequestResponseSchemaTests.cs</tests>
public static class RequestResponseSchema {
  /// <summary>
  /// Complete request_response table definition.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/RequestResponseSchemaTests.cs:Table_ShouldHaveCorrectNameAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/RequestResponseSchemaTests.cs:Table_ShouldDefineCorrectColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/RequestResponseSchemaTests.cs:Table_ShouldDefinePrimaryKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/RequestResponseSchemaTests.cs:Table_ShouldDefineIndexesAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/RequestResponseSchemaTests.cs:Table_CorrelationIdIndex_ShouldBeDefinedAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/RequestResponseSchemaTests.cs:Table_StatusCreatedIndex_ShouldBeDefinedAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/RequestResponseSchemaTests.cs:Table_ExpiresAtIndex_ShouldBeDefinedAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/RequestResponseSchemaTests.cs:Table_StatusColumn_ShouldHaveDefaultValueAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/RequestResponseSchemaTests.cs:Table_CreatedAtColumn_ShouldHaveDefaultValueAsync</tests>
  public static readonly TableDefinition Table = new(
    Name: "request_response",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "request_id",
        DataType: WhizbangDataType.Uuid,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "correlation_id",
        DataType: WhizbangDataType.Uuid,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "request_type",
        DataType: WhizbangDataType.String,
        MaxLength: 500,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "request_data",
        DataType: WhizbangDataType.Json,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "response_type",
        DataType: WhizbangDataType.String,
        MaxLength: 500,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "response_data",
        DataType: WhizbangDataType.Json,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "status",
        DataType: WhizbangDataType.String,
        MaxLength: 50,
        Nullable: false,
        DefaultValue: DefaultValue.String("Pending")
      ),
      new ColumnDefinition(
        Name: "created_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      ),
      new ColumnDefinition(
        Name: "completed_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "expires_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: true
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_request_response_correlation",
        Columns: ImmutableArray.Create("correlation_id"),
        Unique: true  // Required for ON CONFLICT(correlation_id) in Dapper code
      ),
      new IndexDefinition(
        Name: "idx_request_response_status_created",
        Columns: ImmutableArray.Create("status", "created_at")
      ),
      new IndexDefinition(
        Name: "idx_request_response_expires",
        Columns: ImmutableArray.Create("expires_at")
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/RequestResponseSchemaTests.cs:Columns_ShouldProvideTypeConstantsAsync</tests>
  public static class Columns {
    public const string RequestId = "request_id";
    public const string CorrelationId = "correlation_id";
    public const string RequestType = "request_type";
    public const string RequestData = "request_data";
    public const string ResponseType = "response_type";
    public const string ResponseData = "response_data";
    public const string Status = "status";
    public const string CreatedAt = "created_at";
    public const string CompletedAt = "completed_at";
    public const string ExpiresAt = "expires_at";
  }
}
