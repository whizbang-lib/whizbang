using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for MessageDeduplicationSchema - permanent deduplication tracking table definition.
/// Tests verify table structure, columns, indexes, and constraints.
/// </summary>
public class MessageDeduplicationSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_ShouldHaveCorrectNameAsync() {
    // Arrange & Act
    var tableName = MessageDeduplicationSchema.Table.Name;

    // Assert
    await Assert.That(tableName).IsEqualTo("message_deduplication");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineCorrectColumnsAsync() {
    // Arrange & Act
    var columns = MessageDeduplicationSchema.Table.Columns;

    // Assert - Verify column count
    await Assert.That(columns).Count().IsEqualTo(2);

    // Verify message_id column
    var messageId = columns[0];
    await Assert.That(messageId.Name).IsEqualTo("message_id");
    await Assert.That(messageId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(messageId.PrimaryKey).IsTrue();
    await Assert.That(messageId.Nullable).IsFalse();

    // Verify first_seen_at column
    var firstSeenAt = columns[1];
    await Assert.That(firstSeenAt.Name).IsEqualTo("first_seen_at");
    await Assert.That(firstSeenAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(firstSeenAt.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefinePrimaryKeyAsync() {
    // Arrange & Act
    var primaryKeyColumns = MessageDeduplicationSchema.Table.Columns
      .Where(c => c.PrimaryKey)
      .ToList();

    // Assert
    await Assert.That(primaryKeyColumns).Count().IsEqualTo(1);
    await Assert.That(primaryKeyColumns[0].Name).IsEqualTo("message_id");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineIndexesAsync() {
    // Arrange & Act
    var indexes = MessageDeduplicationSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).Count().IsEqualTo(1);

    // Verify first_seen_at index
    var firstSeenIndex = indexes[0];
    await Assert.That(firstSeenIndex.Name).IsEqualTo("idx_message_dedup_first_seen");
    await Assert.That(firstSeenIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(firstSeenIndex.Columns[0]).IsEqualTo("first_seen_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_FirstSeenAtIndex_ShouldBeDefinedAsync() {
    // Arrange & Act
    var firstSeenIndex = MessageDeduplicationSchema.Table.Indexes
      .FirstOrDefault(i => i.Name == "idx_message_dedup_first_seen");

    // Assert
    await Assert.That(firstSeenIndex).IsNotNull();
    await Assert.That(firstSeenIndex!.Columns).Count().IsEqualTo(1);
    await Assert.That(firstSeenIndex.Columns[0]).IsEqualTo("first_seen_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_FirstSeenAtColumn_ShouldHaveDefaultValueAsync() {
    // Arrange & Act
    var firstSeenAtColumn = MessageDeduplicationSchema.Table.Columns
      .FirstOrDefault(c => c.Name == "first_seen_at");

    // Assert
    await Assert.That(firstSeenAtColumn).IsNotNull();
    await Assert.That(firstSeenAtColumn!.DefaultValue).IsNotNull();
    await Assert.That(firstSeenAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)firstSeenAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldBeMinimalAsync() {
    // Arrange & Act
    var columnCount = MessageDeduplicationSchema.Table.Columns.Length;

    // Assert - Exactly 2 columns (message_id, first_seen_at)
    // Rationale: This table grows forever, so extra columns waste space
    await Assert.That(columnCount).IsEqualTo(2);
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_ShouldProvideTypeConstantsAsync() {
    // Arrange & Act
    var messageId = MessageDeduplicationSchema.Columns.MessageId;
    var firstSeenAt = MessageDeduplicationSchema.Columns.FirstSeenAt;

    // Assert - Verify constants match column names
    await Assert.That(messageId).IsEqualTo("message_id");
    await Assert.That(firstSeenAt).IsEqualTo("first_seen_at");

    // Verify constants match table definition
    var columns = MessageDeduplicationSchema.Table.Columns;
    await Assert.That(columns.Any(c => c.Name == messageId)).IsTrue();
    await Assert.That(columns.Any(c => c.Name == firstSeenAt)).IsTrue();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_MessageIdColumn_ShouldNotBeNullableAsync() {
    // Arrange & Act
    var messageIdColumn = MessageDeduplicationSchema.Table.Columns
      .FirstOrDefault(c => c.Name == "message_id");

    // Assert
    await Assert.That(messageIdColumn).IsNotNull();
    await Assert.That(messageIdColumn!.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_FirstSeenAtColumn_ShouldNotBeNullableAsync() {
    // Arrange & Act
    var firstSeenAtColumn = MessageDeduplicationSchema.Table.Columns
      .FirstOrDefault(c => c.Name == "first_seen_at");

    // Assert
    await Assert.That(firstSeenAtColumn).IsNotNull();
    await Assert.That(firstSeenAtColumn!.Nullable).IsFalse();
  }
}
