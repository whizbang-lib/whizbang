using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for wh_lifecycle_completions table.
/// Durable marker recording that PostLifecycle fired for an event.
/// Used by startup reconciliation to detect events where perspectives
/// completed but PostLifecycle was not fired (e.g., due to process crash).
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-reconciliation</docs>
public static class LifecycleCompletionsSchema {
  public const string TABLE_NAME = "lifecycle_completions";

  /// <summary>
  /// Complete lifecycle_completions table definition.
  /// Lightweight marker table: one row per event that completed PostLifecycle.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: TABLE_NAME,
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: Columns.EVENT_ID,
        DataType: WhizbangDataType.UUID,
        Nullable: false,
        PrimaryKey: true
      ),
      new ColumnDefinition(
        Name: Columns.INSTANCE_ID,
        DataType: WhizbangDataType.UUID,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: Columns.COMPLETED_AT,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      )
    ),
    Indexes: [
      new IndexDefinition(
        Name: "idx_lifecycle_completions_completed_at",
        Columns: [Columns.COMPLETED_AT]
      )
    ]
  );

  /// <summary>Column name constants for type-safe access.</summary>
  public static class Columns {
    public const string EVENT_ID = "event_id";
    public const string INSTANCE_ID = "instance_id";
    public const string COMPLETED_AT = "completed_at";
  }
}
