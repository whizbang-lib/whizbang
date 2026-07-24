using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for MessageTypeRegistrySchema — universal catalog of registered message/perspective
/// types with optional [PinnedId] metadata. Every type gets a DB-generated type_id; pinned_id
/// is populated only when declared and enforced by a partial unique index.
/// </summary>
public class MessageTypeRegistrySchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_ShouldHaveCorrectNameAsync() {
    await Assert.That(MessageTypeRegistrySchema.Table.Name).IsEqualTo("message_type_registry");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineFiveColumnsAsync() {
    await Assert.That(MessageTypeRegistrySchema.Table.Columns).Count().IsEqualTo(5);
  }

  [Test]
  [Category("Schema")]
  public async Task TypeId_IsUuidPrimaryKeyWithDatabaseGenerationAsync() {
    var typeId = MessageTypeRegistrySchema.Table.Columns
        .FirstOrDefault(c => c.Name == "type_id");

    await Assert.That(typeId).IsNotNull();
    await Assert.That(typeId!.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(typeId.PrimaryKey).IsTrue();
    await Assert.That(typeId.Nullable).IsFalse();
    await Assert.That(typeId.DefaultValue).IsNotNull();
    await Assert.That(typeId.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)typeId.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.UUID__GENERATE);
  }

  [Test]
  [Category("Schema")]
  public async Task ClrTypeName_IsNonNullStringWithMaxLengthAsync() {
    var clrTypeName = MessageTypeRegistrySchema.Table.Columns
        .FirstOrDefault(c => c.Name == "clr_type_name");

    await Assert.That(clrTypeName).IsNotNull();
    await Assert.That(clrTypeName!.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(clrTypeName.Nullable).IsFalse();
    await Assert.That(clrTypeName.MaxLength).IsEqualTo(500);
  }

  [Test]
  [Category("Schema")]
  public async Task PinnedId_IsNullableUuidAsync() {
    var pinnedId = MessageTypeRegistrySchema.Table.Columns
        .FirstOrDefault(c => c.Name == "pinned_id");

    await Assert.That(pinnedId).IsNotNull();
    await Assert.That(pinnedId!.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(pinnedId.Nullable).IsTrue();
    await Assert.That(pinnedId.PrimaryKey).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Kind_IsNonNullBoundedStringAsync() {
    var kind = MessageTypeRegistrySchema.Table.Columns
        .FirstOrDefault(c => c.Name == "kind");

    await Assert.That(kind).IsNotNull();
    await Assert.That(kind!.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(kind.Nullable).IsFalse();
    await Assert.That(kind.MaxLength).IsEqualTo(50);
  }

  [Test]
  [Category("Schema")]
  public async Task UpdatedAt_IsNonNullTimestampTzWithNowDefaultAsync() {
    var updatedAt = MessageTypeRegistrySchema.Table.Columns
        .FirstOrDefault(c => c.Name == "updated_at");

    await Assert.That(updatedAt).IsNotNull();
    await Assert.That(updatedAt!.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(updatedAt.Nullable).IsFalse();
    await Assert.That(updatedAt.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)updatedAt.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);
  }

  [Test]
  [Category("Schema")]
  public async Task ClrTypeName_HasUniqueConstraintAsync() {
    var constraint = MessageTypeRegistrySchema.Table.UniqueConstraints
        .FirstOrDefault(c => c.Columns.Length == 1 && c.Columns[0] == "clr_type_name");

    await Assert.That(constraint).IsNotNull();
  }

  [Test]
  [Category("Schema")]
  public async Task PinnedId_HasPartialUniqueIndexWhereNotNullAsync() {
    // The partial unique index is what enforces "no two types share a pinned_id" while
    // still allowing any number of rows with null pinned_id.
    var pinnedIdIndex = MessageTypeRegistrySchema.Table.Indexes
        .FirstOrDefault(i => i.Columns.Length == 1 && i.Columns[0] == "pinned_id");

    await Assert.That(pinnedIdIndex).IsNotNull();
    await Assert.That(pinnedIdIndex!.Unique).IsTrue();
    await Assert.That(pinnedIdIndex.WhereClause).IsEqualTo("pinned_id IS NOT NULL");
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_ShouldProvideNameConstantsAsync() {
    var typeId = MessageTypeRegistrySchema.Columns.TYPE_ID;
    var clrTypeName = MessageTypeRegistrySchema.Columns.CLR_TYPE_NAME;
    var pinnedId = MessageTypeRegistrySchema.Columns.PINNED_ID;
    var kind = MessageTypeRegistrySchema.Columns.KIND;
    var updatedAt = MessageTypeRegistrySchema.Columns.UPDATED_AT;

    await Assert.That(typeId).IsEqualTo("type_id");
    await Assert.That(clrTypeName).IsEqualTo("clr_type_name");
    await Assert.That(pinnedId).IsEqualTo("pinned_id");
    await Assert.That(kind).IsEqualTo("kind");
    await Assert.That(updatedAt).IsEqualTo("updated_at");
  }
}
