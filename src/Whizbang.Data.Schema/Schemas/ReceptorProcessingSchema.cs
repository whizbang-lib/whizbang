using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the receptor_processing table (event handler tracking).
/// Table name: {prefix}receptor_processing (e.g., wh_receptor_processing)
/// Tracks which receptors have processed which events (log-style, many receptors per event).
/// Used for independent event handlers that don't require ordering.
/// </summary>
public static class ReceptorProcessingSchema {
  /// <summary>
  /// Complete receptor_processing table definition.
  /// NOTE: Unique constraint (event_id, receptor_name) and foreign key constraints
  /// are not yet supported by TableDefinition and must be added in Phase 2.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: "receptor_processing",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "id",
        DataType: WhizbangDataType.Uuid,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "event_id",
        DataType: WhizbangDataType.Uuid,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "receptor_name",
        DataType: WhizbangDataType.String,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "status",
        DataType: WhizbangDataType.SmallInt,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(0)
      ),
      new ColumnDefinition(
        Name: "attempts",
        DataType: WhizbangDataType.Integer,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(0)
      ),
      new ColumnDefinition(
        Name: "error",
        DataType: WhizbangDataType.String,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "started_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      ),
      new ColumnDefinition(
        Name: "processed_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: true
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_receptor_processing_event_id",
        Columns: ImmutableArray.Create("event_id")
      ),
      new IndexDefinition(
        Name: "idx_receptor_processing_receptor_name",
        Columns: ImmutableArray.Create("receptor_name")
      ),
      new IndexDefinition(
        Name: "idx_receptor_processing_status",
        Columns: ImmutableArray.Create("status")
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  public static class Columns {
    public const string Id = "id";
    public const string EventId = "event_id";
    public const string ReceptorName = "receptor_name";
    public const string Status = "status";
    public const string Attempts = "attempts";
    public const string Error = "error";
    public const string StartedAt = "started_at";
    public const string ProcessedAt = "processed_at";
  }
}
