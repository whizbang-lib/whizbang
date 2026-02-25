using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the perspective_registry table (CLR type to table name mappings).
/// Table name: {prefix}perspective_registry (e.g., wh_perspective_registry)
/// Stores mappings between CLR types and their perspective table names with full schema JSON.
/// Used for schema drift detection and auto-migration when table names change.
/// </summary>
/// <docs>perspectives/registry</docs>
public static class PerspectiveRegistrySchema {
  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  public static class Columns {
    public const string ID = "id";
    public const string CLR_TYPE_NAME = "clr_type_name";
    public const string TABLE_NAME = "table_name";
    public const string SCHEMA_JSON = "schema_json";
    public const string SCHEMA_HASH = "schema_hash";
    public const string SERVICE_NAME = "service_name";
    public const string CREATED_AT = "created_at";
    public const string UPDATED_AT = "updated_at";
  }

  /// <summary>
  /// Complete perspective_registry table definition.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: "perspective_registry",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: Columns.ID,
        DataType: WhizbangDataType.UUID,
        PrimaryKey: true,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.UUID__GENERATE)
      ),
      new ColumnDefinition(
        Name: Columns.CLR_TYPE_NAME,
        DataType: WhizbangDataType.STRING,
        MaxLength: 500,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: Columns.TABLE_NAME,
        DataType: WhizbangDataType.STRING,
        MaxLength: 255,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: Columns.SCHEMA_JSON,
        DataType: WhizbangDataType.JSON,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: Columns.SCHEMA_HASH,
        DataType: WhizbangDataType.STRING,
        MaxLength: 64,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: Columns.SERVICE_NAME,
        DataType: WhizbangDataType.STRING,
        MaxLength: 255,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: Columns.CREATED_AT,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      ),
      new ColumnDefinition(
        Name: Columns.UPDATED_AT,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_perspective_registry_table_name",
        Columns: ImmutableArray.Create(Columns.TABLE_NAME)
      ),
      new IndexDefinition(
        Name: "idx_perspective_registry_schema_hash",
        Columns: ImmutableArray.Create(Columns.SCHEMA_HASH)
      ),
      new IndexDefinition(
        Name: "idx_perspective_registry_service_name",
        Columns: ImmutableArray.Create(Columns.SERVICE_NAME)
      )
    ),
    UniqueConstraints: ImmutableArray.Create(
      new UniqueConstraintDefinition(
        Name: "uq_perspective_registry_type_service",
        Columns: ImmutableArray.Create(Columns.CLR_TYPE_NAME, Columns.SERVICE_NAME)
      )
    )
  );
}
