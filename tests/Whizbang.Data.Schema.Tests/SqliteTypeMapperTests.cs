using Whizbang.Data.Schema.Sqlite;

namespace Whizbang.Data.Schema.Tests;

/// <summary>
/// Tests for SqliteTypeMapper - maps database-agnostic types to SQLite-specific SQL types.
/// </summary>
public class SqliteTypeMapperTests {
  [Test]
  public async Task MapDataType_Uuid_ReturnsTextAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.Uuid);

    // Assert
    await Assert.That(result).IsEqualTo("TEXT");
  }

  [Test]
  public async Task MapDataType_String_ReturnsTextAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.String);

    // Assert
    await Assert.That(result).IsEqualTo("TEXT");
  }

  [Test]
  public async Task MapDataType_StringWithMaxLength_ReturnsTextAsync() {
    // Arrange & Act
    // SQLite doesn't enforce length constraints, so VARCHAR(n) becomes TEXT
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.String, maxLength: 255);

    // Assert
    await Assert.That(result).IsEqualTo("TEXT");
  }

  [Test]
  public async Task MapDataType_TimestampTz_ReturnsTextAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.TimestampTz);

    // Assert
    await Assert.That(result).IsEqualTo("TEXT");
  }

  [Test]
  public async Task MapDataType_Json_ReturnsTextAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.Json);

    // Assert
    await Assert.That(result).IsEqualTo("TEXT");
  }

  [Test]
  public async Task MapDataType_BigInt_ReturnsIntegerAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.BigInt);

    // Assert
    await Assert.That(result).IsEqualTo("INTEGER");
  }

  [Test]
  public async Task MapDataType_Integer_ReturnsIntegerAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.Integer);

    // Assert
    await Assert.That(result).IsEqualTo("INTEGER");
  }

  [Test]
  public async Task MapDataType_Boolean_ReturnsIntegerAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.Boolean);

    // Assert
    await Assert.That(result).IsEqualTo("INTEGER");
  }

  [Test]
  public async Task MapDefaultValue_FunctionDateTimeNow_ReturnsCurrentTimestampAsync() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.DateTime_Now);

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("CURRENT_TIMESTAMP");
  }

  [Test]
  public async Task MapDefaultValue_FunctionDateTimeUtcNow_ReturnsDatetimeUtcAsync() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.DateTime_UtcNow);

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("(datetime('now', 'utc'))");
  }

  [Test]
  public async Task MapDefaultValue_FunctionUuidGenerate_ReturnsLowerHexAsync() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.Uuid_Generate);

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    // SQLite stores UUIDs as TEXT, application must generate
    await Assert.That(result).IsEqualTo("(lower(hex(randomblob(16))))");
  }

  [Test]
  public async Task MapDefaultValue_FunctionBooleanTrue_Returns1Async() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.Boolean_True);

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("1");
  }

  [Test]
  public async Task MapDefaultValue_FunctionBooleanFalse_Returns0Async() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.Boolean_False);

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("0");
  }

  [Test]
  public async Task MapDefaultValue_Integer_ReturnsIntegerStringAsync() {
    // Arrange
    var defaultValue = DefaultValue.Integer(42);

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("42");
  }

  [Test]
  public async Task MapDefaultValue_String_ReturnsQuotedStringAsync() {
    // Arrange
    var defaultValue = DefaultValue.String("Pending");

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("'Pending'");
  }

  [Test]
  public async Task MapDefaultValue_StringWithSingleQuote_EscapesSingleQuoteAsync() {
    // Arrange
    var defaultValue = DefaultValue.String("O'Reilly");

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("'O''Reilly'");
  }

  [Test]
  public async Task MapDefaultValue_BooleanTrue_Returns1Async() {
    // Arrange
    var defaultValue = DefaultValue.Boolean(true);

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("1");
  }

  [Test]
  public async Task MapDefaultValue_BooleanFalse_Returns0Async() {
    // Arrange
    var defaultValue = DefaultValue.Boolean(false);

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("0");
  }

  [Test]
  public async Task MapDefaultValue_Null_ReturnsNullAsync() {
    // Arrange
    var defaultValue = DefaultValue.Null;

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("NULL");
  }
}
