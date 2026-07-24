using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the message_type_registry table — a universal catalog of every
/// registered IMessage / IPerspectiveFor&lt;&gt; type with optional pinned-id metadata.
/// Table name: {prefix}message_type_registry (e.g., wh_message_type_registry).
/// </summary>
/// <remarks>
/// Every registered type gets a DB-generated <c>type_id</c> and a row keyed by
/// <c>clr_type_name</c>. The <c>pinned_id</c> column is populated only when the type
/// carries <c>[PinnedId]</c>; a partial unique index enforces that no two types share
/// a pinned ID while allowing any number of unpinned rows with null.
/// </remarks>
/// <docs>core-concepts/pinned-identity</docs>
public static class MessageTypeRegistrySchema {
  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  public static class Columns {
    public const string TYPE_ID = "type_id";
    public const string CLR_TYPE_NAME = "clr_type_name";
    public const string PINNED_ID = "pinned_id";
    public const string KIND = "kind";
    public const string UPDATED_AT = "updated_at";
  }

  /// <summary>
  /// Complete message_type_registry table definition.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: "message_type_registry",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: Columns.TYPE_ID,
        DataType: WhizbangDataType.UUID,
        Nullable: false,
        PrimaryKey: true,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.UUID__GENERATE)
      ),
      new ColumnDefinition(
        Name: Columns.CLR_TYPE_NAME,
        DataType: WhizbangDataType.STRING,
        Nullable: false,
        MaxLength: 500
      ),
      new ColumnDefinition(
        Name: Columns.PINNED_ID,
        DataType: WhizbangDataType.UUID,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: Columns.KIND,
        DataType: WhizbangDataType.STRING,
        Nullable: false,
        MaxLength: 50
      ),
      new ColumnDefinition(
        Name: Columns.UPDATED_AT,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      )
    ),
    Indexes: [
      new IndexDefinition(
        Name: "ix_message_type_registry_pinned_id",
        Columns: [Columns.PINNED_ID],
        Unique: true,
        WhereClause: "pinned_id IS NOT NULL"
      )
    ],
    UniqueConstraints: [
      new UniqueConstraintDefinition(
        Name: "uq_message_type_registry_clr_type_name",
        Columns: [Columns.CLR_TYPE_NAME]
      )
    ]
  );
}
