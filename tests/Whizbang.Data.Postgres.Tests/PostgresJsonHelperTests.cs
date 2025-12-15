using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Tests for PostgresJsonHelper methods.
/// Validates AOT-compatible JSONB parameter creation for PostgreSQL.
/// </summary>
public class PostgresJsonHelperTests {
  /// <summary>
  /// Test: JsonStringToJsonb with valid JSON string creates JSONB parameter
  /// </summary>
  [Test]
  public async Task JsonStringToJsonb_ValidJsonString_CreatesJsonbParameterAsync() {
    // Arrange
    const string json = "{\"name\":\"test\",\"value\":42}";

    // Act
    var param = PostgresJsonHelper.JsonStringToJsonb(json);

    // Assert
    await Assert.That(param.Value).IsEqualTo(json);
    await Assert.That(param.NpgsqlDbType).IsEqualTo(NpgsqlDbType.Jsonb);
  }

  /// <summary>
  /// Test: JsonStringToJsonb with null input creates null JSONB parameter
  /// </summary>
  [Test]
  public async Task JsonStringToJsonb_NullInput_CreatesNullJsonbParameterAsync() {
    // Arrange
    string? json = null;

    // Act
    var param = PostgresJsonHelper.JsonStringToJsonb(json);

    // Assert
    await Assert.That(param.Value).IsEqualTo("null");
    await Assert.That(param.NpgsqlDbType).IsEqualTo(NpgsqlDbType.Jsonb);
  }

  /// <summary>
  /// Test: EmptyJsonbArray creates empty JSONB array parameter
  /// </summary>
  [Test]
  public async Task EmptyJsonbArray_CreatesEmptyArrayParameterAsync() {
    // Arrange & Act
    var param = PostgresJsonHelper.EmptyJsonbArray();

    // Assert
    await Assert.That(param.Value).IsEqualTo("[]");
    await Assert.That(param.NpgsqlDbType).IsEqualTo(NpgsqlDbType.Jsonb);
  }

  /// <summary>
  /// Test: NullJsonb creates null JSONB parameter
  /// </summary>
  [Test]
  public async Task NullJsonb_CreatesNullJsonbParameterAsync() {
    // Arrange & Act
    var param = PostgresJsonHelper.NullJsonb();

    // Assert
    await Assert.That(param.Value).IsEqualTo("null");
    await Assert.That(param.NpgsqlDbType).IsEqualTo(NpgsqlDbType.Jsonb);
  }
}
