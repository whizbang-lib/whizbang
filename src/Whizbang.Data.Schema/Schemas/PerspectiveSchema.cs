using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Factory for creating perspective table definitions dynamically.
/// Perspective tables use the perspective prefix (e.g., wb_per_product_dto).
/// Each perspective can define its own columns and indexes for read-optimized data.
/// </summary>
public static class PerspectiveSchema {
  /// <summary>
  /// Creates a perspective table definition with custom columns and indexes.
  /// </summary>
  /// <param name="name">Table name without prefix (e.g., "product_dto")</param>
  /// <param name="columns">Column definitions for the perspective</param>
  /// <param name="indexes">Optional index definitions (default: empty)</param>
  /// <returns>Complete table definition for the perspective</returns>
  public static TableDefinition CreateTable(
    string name,
    ImmutableArray<ColumnDefinition> columns,
    ImmutableArray<IndexDefinition>? indexes = null) {

    return new TableDefinition(
      Name: name,
      Columns: columns,
      Indexes: indexes ?? ImmutableArray<IndexDefinition>.Empty
    );
  }

  /// <summary>
  /// Creates a simple perspective table with an ID column and custom additional columns.
  /// Automatically adds a primary key UUID column named "id".
  /// </summary>
  /// <param name="name">Table name without prefix (e.g., "product_dto")</param>
  /// <param name="additionalColumns">Additional columns beyond the ID</param>
  /// <param name="indexes">Optional index definitions</param>
  /// <returns>Complete table definition with ID column + additional columns</returns>
  public static TableDefinition CreateTableWithId(
    string name,
    ImmutableArray<ColumnDefinition> additionalColumns,
    ImmutableArray<IndexDefinition>? indexes = null) {

    var idColumn = new ColumnDefinition(
      Name: "id",
      DataType: WhizbangDataType.Uuid,
      PrimaryKey: true,
      Nullable: false
    );

    var allColumns = ImmutableArray.Create(idColumn).AddRange(additionalColumns);

    return new TableDefinition(
      Name: name,
      Columns: allColumns,
      Indexes: indexes ?? ImmutableArray<IndexDefinition>.Empty
    );
  }

  /// <summary>
  /// Common column definitions that can be reused across perspectives.
  /// </summary>
  public static class CommonColumns {
    /// <summary>
    /// UUID primary key column named "id".
    /// </summary>
    public static readonly ColumnDefinition Id = new(
      Name: "id",
      DataType: WhizbangDataType.Uuid,
      PrimaryKey: true,
      Nullable: false
    );

    /// <summary>
    /// Timestamp column for when the record was created.
    /// </summary>
    public static readonly ColumnDefinition CreatedAt = new(
      Name: "created_at",
      DataType: WhizbangDataType.TimestampTz,
      Nullable: false,
      DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
    );

    /// <summary>
    /// Timestamp column for when the record was last updated.
    /// </summary>
    public static readonly ColumnDefinition UpdatedAt = new(
      Name: "updated_at",
      DataType: WhizbangDataType.TimestampTz,
      Nullable: true
    );

    /// <summary>
    /// Version column for optimistic concurrency control.
    /// </summary>
    public static readonly ColumnDefinition Version = new(
      Name: "version",
      DataType: WhizbangDataType.Integer,
      Nullable: false,
      DefaultValue: DefaultValue.Integer(1)
    );

    /// <summary>
    /// Soft delete timestamp column.
    /// </summary>
    public static readonly ColumnDefinition DeletedAt = new(
      Name: "deleted_at",
      DataType: WhizbangDataType.TimestampTz,
      Nullable: true
    );
  }
}
