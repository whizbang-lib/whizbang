using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for RequestResponseSchema - request/response tracking table definition.
/// Tests verify table structure, columns, indexes, and constraints.
/// </summary>
public class RequestResponseSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_ShouldHaveCorrectNameAsync() {
    // Arrange & Act
    var tableName = RequestResponseSchema.Table.Name;

    // Assert
    await Assert.That(tableName).IsEqualTo("request_response");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineCorrectColumnsAsync() {
    // Arrange & Act
    var columns = RequestResponseSchema.Table.Columns;

    // Assert - Verify column count
    await Assert.That(columns).HasCount().EqualTo(10);

    // Verify each column definition
    var requestId = columns[0];
    await Assert.That(requestId.Name).IsEqualTo("request_id");
    await Assert.That(requestId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(requestId.PrimaryKey).IsTrue();
    await Assert.That(requestId.Nullable).IsFalse();

    var correlationId = columns[1];
    await Assert.That(correlationId.Name).IsEqualTo("correlation_id");
    await Assert.That(correlationId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(correlationId.Nullable).IsFalse();

    var requestType = columns[2];
    await Assert.That(requestType.Name).IsEqualTo("request_type");
    await Assert.That(requestType.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(requestType.MaxLength).IsEqualTo(500);
    await Assert.That(requestType.Nullable).IsFalse();

    var requestData = columns[3];
    await Assert.That(requestData.Name).IsEqualTo("request_data");
    await Assert.That(requestData.DataType).IsEqualTo(WhizbangDataType.Json);
    await Assert.That(requestData.Nullable).IsFalse();

    var responseType = columns[4];
    await Assert.That(responseType.Name).IsEqualTo("response_type");
    await Assert.That(responseType.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(responseType.MaxLength).IsEqualTo(500);
    await Assert.That(responseType.Nullable).IsTrue();

    var responseData = columns[5];
    await Assert.That(responseData.Name).IsEqualTo("response_data");
    await Assert.That(responseData.DataType).IsEqualTo(WhizbangDataType.Json);
    await Assert.That(responseData.Nullable).IsTrue();

    var status = columns[6];
    await Assert.That(status.Name).IsEqualTo("status");
    await Assert.That(status.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(status.MaxLength).IsEqualTo(50);
    await Assert.That(status.Nullable).IsFalse();

    var createdAt = columns[7];
    await Assert.That(createdAt.Name).IsEqualTo("created_at");
    await Assert.That(createdAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(createdAt.Nullable).IsFalse();

    var completedAt = columns[8];
    await Assert.That(completedAt.Name).IsEqualTo("completed_at");
    await Assert.That(completedAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(completedAt.Nullable).IsTrue();

    var expiresAt = columns[9];
    await Assert.That(expiresAt.Name).IsEqualTo("expires_at");
    await Assert.That(expiresAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(expiresAt.Nullable).IsTrue();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefinePrimaryKeyAsync() {
    // Arrange & Act
    var primaryKeyColumns = RequestResponseSchema.Table.Columns
      .Where(c => c.PrimaryKey)
      .ToList();

    // Assert
    await Assert.That(primaryKeyColumns).HasCount().EqualTo(1);
    await Assert.That(primaryKeyColumns[0].Name).IsEqualTo("request_id");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineIndexesAsync() {
    // Arrange & Act
    var indexes = RequestResponseSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).HasCount().EqualTo(3);

    // Verify correlation index
    var correlationIndex = indexes[0];
    await Assert.That(correlationIndex.Name).IsEqualTo("idx_request_response_correlation");
    await Assert.That(correlationIndex.Columns).HasCount().EqualTo(1);
    await Assert.That(correlationIndex.Columns[0]).IsEqualTo("correlation_id");

    // Verify status/created_at composite index
    var statusCreatedIndex = indexes[1];
    await Assert.That(statusCreatedIndex.Name).IsEqualTo("idx_request_response_status_created");
    await Assert.That(statusCreatedIndex.Columns).HasCount().EqualTo(2);
    await Assert.That(statusCreatedIndex.Columns[0]).IsEqualTo("status");
    await Assert.That(statusCreatedIndex.Columns[1]).IsEqualTo("created_at");

    // Verify expires_at index
    var expiresIndex = indexes[2];
    await Assert.That(expiresIndex.Name).IsEqualTo("idx_request_response_expires");
    await Assert.That(expiresIndex.Columns).HasCount().EqualTo(1);
    await Assert.That(expiresIndex.Columns[0]).IsEqualTo("expires_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_CorrelationIdIndex_ShouldBeDefinedAsync() {
    // Arrange & Act
    var correlationIndex = RequestResponseSchema.Table.Indexes
      .FirstOrDefault(i => i.Name == "idx_request_response_correlation");

    // Assert
    await Assert.That(correlationIndex).IsNotNull();
    await Assert.That(correlationIndex!.Columns).HasCount().EqualTo(1);
    await Assert.That(correlationIndex.Columns[0]).IsEqualTo("correlation_id");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_StatusCreatedIndex_ShouldBeDefinedAsync() {
    // Arrange & Act
    var statusIndex = RequestResponseSchema.Table.Indexes
      .FirstOrDefault(i => i.Name == "idx_request_response_status_created");

    // Assert
    await Assert.That(statusIndex).IsNotNull();
    await Assert.That(statusIndex!.Columns).HasCount().EqualTo(2);
    await Assert.That(statusIndex.Columns[0]).IsEqualTo("status");
    await Assert.That(statusIndex.Columns[1]).IsEqualTo("created_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ExpiresAtIndex_ShouldBeDefinedAsync() {
    // Arrange & Act
    var expiresIndex = RequestResponseSchema.Table.Indexes
      .FirstOrDefault(i => i.Name == "idx_request_response_expires");

    // Assert
    await Assert.That(expiresIndex).IsNotNull();
    await Assert.That(expiresIndex!.Columns).HasCount().EqualTo(1);
    await Assert.That(expiresIndex.Columns[0]).IsEqualTo("expires_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_StatusColumn_ShouldHaveDefaultValueAsync() {
    // Arrange & Act
    var statusColumn = RequestResponseSchema.Table.Columns
      .FirstOrDefault(c => c.Name == "status");

    // Assert
    await Assert.That(statusColumn).IsNotNull();
    await Assert.That(statusColumn!.DefaultValue).IsNotNull();
    await Assert.That(statusColumn.DefaultValue).IsTypeOf<StringDefault>();
    await Assert.That(((StringDefault)statusColumn.DefaultValue!).Value).IsEqualTo("Pending");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_CreatedAtColumn_ShouldHaveDefaultValueAsync() {
    // Arrange & Act
    var createdAtColumn = RequestResponseSchema.Table.Columns
      .FirstOrDefault(c => c.Name == "created_at");

    // Assert
    await Assert.That(createdAtColumn).IsNotNull();
    await Assert.That(createdAtColumn!.DefaultValue).IsNotNull();
    await Assert.That(createdAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)createdAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_ShouldProvideTypeConstantsAsync() {
    // Arrange & Act - Access all column constants
    var requestId = RequestResponseSchema.Columns.RequestId;
    var correlationId = RequestResponseSchema.Columns.CorrelationId;
    var requestType = RequestResponseSchema.Columns.RequestType;
    var requestData = RequestResponseSchema.Columns.RequestData;
    var responseType = RequestResponseSchema.Columns.ResponseType;
    var responseData = RequestResponseSchema.Columns.ResponseData;
    var status = RequestResponseSchema.Columns.Status;
    var createdAt = RequestResponseSchema.Columns.CreatedAt;
    var completedAt = RequestResponseSchema.Columns.CompletedAt;
    var expiresAt = RequestResponseSchema.Columns.ExpiresAt;

    // Assert - Verify constants match actual column names
    await Assert.That(requestId).IsEqualTo("request_id");
    await Assert.That(correlationId).IsEqualTo("correlation_id");
    await Assert.That(requestType).IsEqualTo("request_type");
    await Assert.That(requestData).IsEqualTo("request_data");
    await Assert.That(responseType).IsEqualTo("response_type");
    await Assert.That(responseData).IsEqualTo("response_data");
    await Assert.That(status).IsEqualTo("status");
    await Assert.That(createdAt).IsEqualTo("created_at");
    await Assert.That(completedAt).IsEqualTo("completed_at");
    await Assert.That(expiresAt).IsEqualTo("expires_at");
  }
}
