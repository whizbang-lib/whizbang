using Whizbang.Data.Dapper.Postgres.Schema;

namespace Whizbang.Data.Schema.Tests;

/// <summary>
/// Tests for PostgresTypeMapper - maps database-agnostic types to Postgres-specific SQL types.
/// </summary>
public class PostgresTypeMapperTests {
  [Test]
  public async Task MapDataType_Uuid_ReturnsUuidAsync() {
    // Arrange & Act
    var result = PostgresTypeMapper.MapDataType(WhizbangDataType.Uuid);

    // Assert
    await Assert.That(result).IsEqualTo("UUID");
  }

  [Test]
  public async Task MapDataType_String_ReturnsTextAsync() {
    // Arrange & Act
    var result = PostgresTypeMapper.MapDataType(WhizbangDataType.String);

    // Assert
    await Assert.That(result).IsEqualTo("TEXT");
  }

  [Test]
  public async Task MapDataType_StringWithMaxLength_ReturnsVarcharAsync() {
    // Arrange & Act
    var result = PostgresTypeMapper.MapDataType(WhizbangDataType.String, maxLength: 255);

    // Assert
    await Assert.That(result).IsEqualTo("VARCHAR(255)");
  }

  [Test]
  public async Task MapDataType_TimestampTz_ReturnsTimestamptzAsync() {
    // Arrange & Act
    var result = PostgresTypeMapper.MapDataType(WhizbangDataType.TimestampTz);

    // Assert
    await Assert.That(result).IsEqualTo("TIMESTAMPTZ");
  }

  [Test]
  public async Task MapDataType_Json_ReturnsJsonbAsync() {
    // Arrange & Act
    var result = PostgresTypeMapper.MapDataType(WhizbangDataType.Json);

    // Assert
    await Assert.That(result).IsEqualTo("JSONB");
  }

  [Test]
  public async Task MapDataType_BigInt_ReturnsBigintAsync() {
    // Arrange & Act
    var result = PostgresTypeMapper.MapDataType(WhizbangDataType.BigInt);

    // Assert
    await Assert.That(result).IsEqualTo("BIGINT");
  }

  [Test]
  public async Task MapDataType_Integer_ReturnsIntegerAsync() {
    // Arrange & Act
    var result = PostgresTypeMapper.MapDataType(WhizbangDataType.Integer);

    // Assert
    await Assert.That(result).IsEqualTo("INTEGER");
  }

  [Test]
  public async Task MapDataType_Boolean_ReturnsBooleanAsync() {
    // Arrange & Act
    var result = PostgresTypeMapper.MapDataType(WhizbangDataType.Boolean);

    // Assert
    await Assert.That(result).IsEqualTo("BOOLEAN");
  }

  [Test]
  public async Task MapDefaultValue_FunctionDateTimeNow_ReturnsCurrentTimestampAsync() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.DateTime_Now);

    // Act
    var result = PostgresTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("CURRENT_TIMESTAMP");
  }

  [Test]
  public async Task MapDefaultValue_FunctionDateTimeUtcNow_ReturnsUtcExpressionAsync() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.DateTime_UtcNow);

    // Act
    var result = PostgresTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("(NOW() AT TIME ZONE 'UTC')");
  }

  [Test]
  public async Task MapDefaultValue_FunctionUuidGenerate_ReturnsGenRandomUuidAsync() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.Uuid_Generate);

    // Act
    var result = PostgresTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("gen_random_uuid()");
  }

  [Test]
  public async Task MapDefaultValue_FunctionBooleanTrue_ReturnsTrueAsync() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.Boolean_True);

    // Act
    var result = PostgresTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("TRUE");
  }

  [Test]
  public async Task MapDefaultValue_FunctionBooleanFalse_ReturnsFalseAsync() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.Boolean_False);

    // Act
    var result = PostgresTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("FALSE");
  }

  [Test]
  public async Task MapDefaultValue_Integer_ReturnsIntegerStringAsync() {
    // Arrange
    var defaultValue = DefaultValue.Integer(42);

    // Act
    var result = PostgresTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("42");
  }

  [Test]
  public async Task MapDefaultValue_String_ReturnsQuotedStringAsync() {
    // Arrange
    var defaultValue = DefaultValue.String("Pending");

    // Act
    var result = PostgresTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("'Pending'");
  }

  [Test]
  public async Task MapDefaultValue_StringWithSingleQuote_EscapesSingleQuoteAsync() {
    // Arrange
    var defaultValue = DefaultValue.String("O'Reilly");

    // Act
    var result = PostgresTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("'O''Reilly'");
  }

  [Test]
  public async Task MapDefaultValue_BooleanTrue_ReturnsTrueAsync() {
    // Arrange
    var defaultValue = DefaultValue.Boolean(true);

    // Act
    var result = PostgresTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("TRUE");
  }

  [Test]
  public async Task MapDefaultValue_BooleanFalse_ReturnsFalseAsync() {
    // Arrange
    var defaultValue = DefaultValue.Boolean(false);

    // Act
    var result = PostgresTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("FALSE");
  }

  [Test]
  public async Task MapDefaultValue_Null_ReturnsNullAsync() {
    // Arrange
    var defaultValue = DefaultValue.Null;

    // Act
    var result = PostgresTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("NULL");
  }
}
