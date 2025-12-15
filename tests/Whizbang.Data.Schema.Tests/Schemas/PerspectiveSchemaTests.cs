using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for PerspectiveSchema - dynamic perspective table factory.
/// Tests verify CreateTable, CreateTableWithId methods and CommonColumns definitions.
/// </summary>

public class PerspectiveSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task CreateTable_WithNameAndColumns_CreatesTableDefinitionAsync() {
    // Arrange
    var columns = ImmutableArray.Create(
      new ColumnDefinition(
        Name: "name",
        DataType: WhizbangDataType.String,
        MaxLength: 100,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "description",
        DataType: WhizbangDataType.String,
        MaxLength: 500,
        Nullable: true
      )
    );

    // Act
    var table = PerspectiveSchema.CreateTable("product_dto", columns);

    // Assert
    await Assert.That(table.Name).IsEqualTo("product_dto");
    await Assert.That(table.Columns).HasCount().EqualTo(2);
    await Assert.That(table.Columns[0].Name).IsEqualTo("name");
    await Assert.That(table.Columns[1].Name).IsEqualTo("description");
    await Assert.That(table.Indexes).HasCount().EqualTo(0);
  }

  [Test]
  [Category("Schema")]
  public async Task CreateTable_WithIndexes_IncludesIndexesAsync() {
    // Arrange
    var columns = ImmutableArray.Create(
      new ColumnDefinition(
        Name: "product_id",
        DataType: WhizbangDataType.Uuid,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "status",
        DataType: WhizbangDataType.String,
        MaxLength: 50,
        Nullable: false
      )
    );

    var indexes = ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_product_status",
        Columns: ImmutableArray.Create("status")
      )
    );

    // Act
    var table = PerspectiveSchema.CreateTable("product_dto", columns, indexes);

    // Assert
    await Assert.That(table.Name).IsEqualTo("product_dto");
    await Assert.That(table.Indexes).HasCount().EqualTo(1);
    await Assert.That(table.Indexes[0].Name).IsEqualTo("idx_product_status");
    await Assert.That(table.Indexes[0].Columns[0]).IsEqualTo("status");
  }

  [Test]
  [Category("Schema")]
  public async Task CreateTableWithId_AddsIdColumnAsync() {
    // Arrange
    var additionalColumns = ImmutableArray.Create(
      new ColumnDefinition(
        Name: "name",
        DataType: WhizbangDataType.String,
        MaxLength: 100,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "price",
        DataType: WhizbangDataType.Integer,
        Nullable: false
      )
    );

    // Act
    var table = PerspectiveSchema.CreateTableWithId("product_dto", additionalColumns);

    // Assert
    await Assert.That(table.Name).IsEqualTo("product_dto");
    await Assert.That(table.Columns).HasCount().EqualTo(3);

    // Verify ID column is first
    var idColumn = table.Columns[0];
    await Assert.That(idColumn.Name).IsEqualTo("id");
    await Assert.That(idColumn.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(idColumn.PrimaryKey).IsTrue();
    await Assert.That(idColumn.Nullable).IsFalse();

    // Verify additional columns follow
    await Assert.That(table.Columns[1].Name).IsEqualTo("name");
    await Assert.That(table.Columns[2].Name).IsEqualTo("price");
  }

  [Test]
  [Category("Schema")]
  public async Task CommonColumns_Id_HasCorrectDefinitionAsync() {
    // Arrange & Act
    var idColumn = PerspectiveSchema.CommonColumns.Id;

    // Assert
    await Assert.That(idColumn.Name).IsEqualTo("id");
    await Assert.That(idColumn.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(idColumn.PrimaryKey).IsTrue();
    await Assert.That(idColumn.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task CommonColumns_CreatedAt_HasCorrectDefinitionAsync() {
    // Arrange & Act
    var createdAtColumn = PerspectiveSchema.CommonColumns.CreatedAt;

    // Assert
    await Assert.That(createdAtColumn.Name).IsEqualTo("created_at");
    await Assert.That(createdAtColumn.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(createdAtColumn.Nullable).IsFalse();
    await Assert.That(createdAtColumn.DefaultValue).IsNotNull();
    await Assert.That(createdAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)createdAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }

  [Test]
  [Category("Schema")]
  public async Task CommonColumns_UpdatedAt_HasCorrectDefinitionAsync() {
    // Arrange & Act
    var updatedAtColumn = PerspectiveSchema.CommonColumns.UpdatedAt;

    // Assert
    await Assert.That(updatedAtColumn.Name).IsEqualTo("updated_at");
    await Assert.That(updatedAtColumn.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(updatedAtColumn.Nullable).IsTrue();
    await Assert.That(updatedAtColumn.DefaultValue).IsNull();
  }

  [Test]
  [Category("Schema")]
  public async Task CommonColumns_Version_HasCorrectDefinitionAsync() {
    // Arrange & Act
    var versionColumn = PerspectiveSchema.CommonColumns.Version;

    // Assert
    await Assert.That(versionColumn.Name).IsEqualTo("version");
    await Assert.That(versionColumn.DataType).IsEqualTo(WhizbangDataType.Integer);
    await Assert.That(versionColumn.Nullable).IsFalse();
    await Assert.That(versionColumn.DefaultValue).IsNotNull();
    await Assert.That(versionColumn.DefaultValue).IsTypeOf<IntegerDefault>();
    await Assert.That(((IntegerDefault)versionColumn.DefaultValue!).Value).IsEqualTo(1);
  }

  [Test]
  [Category("Schema")]
  public async Task CommonColumns_DeletedAt_HasCorrectDefinitionAsync() {
    // Arrange & Act
    var deletedAtColumn = PerspectiveSchema.CommonColumns.DeletedAt;

    // Assert
    await Assert.That(deletedAtColumn.Name).IsEqualTo("deleted_at");
    await Assert.That(deletedAtColumn.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(deletedAtColumn.Nullable).IsTrue();
    await Assert.That(deletedAtColumn.DefaultValue).IsNull();
  }
}
