using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the message_associations table (message type to consumer mappings).
/// Table name: {prefix}message_associations (e.g., wb_message_associations)
/// Stores associations between message types and their consumers (perspectives, handlers, receptors).
/// Populated during startup via reconciliation to enable auto-creation of perspective checkpoints.
/// </summary>
/// <docs>core-concepts/message-associations</docs>
public static class MessageAssociationsSchema {
  /// <summary>
  /// Complete message_associations table definition.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: "message_associations",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "id",
        DataType: WhizbangDataType.UUID,
        PrimaryKey: true,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.UUID__GENERATE)
      ),
      new ColumnDefinition(
        Name: "message_type",
        DataType: WhizbangDataType.STRING,
        MaxLength: 500,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "association_type",
        DataType: WhizbangDataType.STRING,
        MaxLength: 50,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "target_name",
        DataType: WhizbangDataType.STRING,
        MaxLength: 500,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "service_name",
        DataType: WhizbangDataType.STRING,
        MaxLength: 500,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "created_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      ),
      new ColumnDefinition(
        Name: "updated_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_message_associations_message_type",
        Columns: ImmutableArray.Create("message_type")
      ),
      new IndexDefinition(
        Name: "idx_message_associations_association_type",
        Columns: ImmutableArray.Create("association_type")
      ),
      new IndexDefinition(
        Name: "idx_message_associations_target_name",
        Columns: ImmutableArray.Create("target_name")
      ),
      new IndexDefinition(
        Name: "idx_message_associations_service_name",
        Columns: ImmutableArray.Create("service_name")
      ),
      new IndexDefinition(
        Name: "idx_message_associations_target_lookup",
        Columns: ImmutableArray.Create("association_type", "target_name", "service_name")
      )
    ),
    UniqueConstraints: ImmutableArray.Create(
      new UniqueConstraintDefinition(
        Name: "uq_message_association",
        Columns: ImmutableArray.Create("message_type", "association_type", "target_name", "service_name")
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  public static class Columns {
    public const string ID = "id";
    public const string MESSAGE_TYPE = "message_type";
    public const string ASSOCIATION_TYPE = "association_type";
    public const string TARGET_NAME = "target_name";
    public const string SERVICE_NAME = "service_name";
    public const string CREATED_AT = "created_at";
    public const string UPDATED_AT = "updated_at";
  }
}
