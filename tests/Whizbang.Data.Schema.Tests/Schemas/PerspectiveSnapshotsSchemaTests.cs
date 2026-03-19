using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for PerspectiveSnapshotsSchema - periodic state snapshot table for rewind.
/// Tests verify table structure, column definitions, indexes, and column name constants.
/// </summary>
public class PerspectiveSnapshotsSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectNameAsync() {
    var tableName = PerspectiveSnapshotsSchema.Table.Name;
    await Assert.That(tableName).IsEqualTo("perspective_snapshots");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectColumnsAsync() {
    var columns = PerspectiveSnapshotsSchema.Table.Columns;

    await Assert.That(columns).Count().IsEqualTo(6);

    var streamId = columns.First(c => c.Name == "stream_id");
    await Assert.That(streamId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(streamId.PrimaryKey).IsTrue();
    await Assert.That(streamId.Nullable).IsFalse();

    var perspectiveName = columns.First(c => c.Name == "perspective_name");
    await Assert.That(perspectiveName.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(perspectiveName.PrimaryKey).IsTrue();
    await Assert.That(perspectiveName.Nullable).IsFalse();

    var snapshotEventId = columns.First(c => c.Name == "snapshot_event_id");
    await Assert.That(snapshotEventId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(snapshotEventId.PrimaryKey).IsTrue();
    await Assert.That(snapshotEventId.Nullable).IsFalse();

    var snapshotData = columns.First(c => c.Name == "snapshot_data");
    await Assert.That(snapshotData.DataType).IsEqualTo(WhizbangDataType.JSON);
    await Assert.That(snapshotData.Nullable).IsFalse();

    var sequenceNumber = columns.First(c => c.Name == "sequence_number");
    await Assert.That(sequenceNumber.DataType).IsEqualTo(WhizbangDataType.BIG_INT);
    await Assert.That(sequenceNumber.Nullable).IsFalse();

    var createdAt = columns.First(c => c.Name == "created_at");
    await Assert.That(createdAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(createdAt.Nullable).IsFalse();
    await Assert.That(createdAt.DefaultValue).IsNotNull();
    await Assert.That(createdAt.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)createdAt.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCompositePrimaryKeyAsync() {
    var columns = PerspectiveSnapshotsSchema.Table.Columns;
    var pkColumns = columns.Where(c => c.PrimaryKey).ToArray();

    await Assert.That(pkColumns).Count().IsEqualTo(3);
    await Assert.That(pkColumns.Select(c => c.Name)).Contains("stream_id");
    await Assert.That(pkColumns.Select(c => c.Name)).Contains("perspective_name");
    await Assert.That(pkColumns.Select(c => c.Name)).Contains("snapshot_event_id");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectIndexesAsync() {
    var indexes = PerspectiveSnapshotsSchema.Table.Indexes;

    await Assert.That(indexes).Count().IsEqualTo(1);

    var lookupIndex = indexes[0];
    await Assert.That(lookupIndex.Name).IsEqualTo("idx_perspective_snapshots_lookup");
    await Assert.That(lookupIndex.Columns).Count().IsEqualTo(3);
    await Assert.That(lookupIndex.Columns[0]).IsEqualTo("stream_id");
    await Assert.That(lookupIndex.Columns[1]).IsEqualTo("perspective_name");
    await Assert.That(lookupIndex.Columns[2]).IsEqualTo("sequence_number");
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_Constants_MatchColumnNamesAsync() {
    string streamId = PerspectiveSnapshotsSchema.Columns.STREAM_ID;
    string perspectiveName = PerspectiveSnapshotsSchema.Columns.PERSPECTIVE_NAME;
    string snapshotEventId = PerspectiveSnapshotsSchema.Columns.SNAPSHOT_EVENT_ID;
    string snapshotData = PerspectiveSnapshotsSchema.Columns.SNAPSHOT_DATA;
    string sequenceNumber = PerspectiveSnapshotsSchema.Columns.SEQUENCE_NUMBER;
    string createdAt = PerspectiveSnapshotsSchema.Columns.CREATED_AT;

    await Assert.That(streamId).IsEqualTo("stream_id");
    await Assert.That(perspectiveName).IsEqualTo("perspective_name");
    await Assert.That(snapshotEventId).IsEqualTo("snapshot_event_id");
    await Assert.That(snapshotData).IsEqualTo("snapshot_data");
    await Assert.That(sequenceNumber).IsEqualTo("sequence_number");
    await Assert.That(createdAt).IsEqualTo("created_at");
  }
}
