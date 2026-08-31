using Whizbang.Data.Dapper.Sqlite.Schema;

namespace Whizbang.Data.Schema.Tests;

/// <summary>
/// Tests for SqliteTypeMapper - maps database-agnostic types to SQLite-specific SQL types.
/// </summary>
public class SqliteTypeMapperTests {
  [Test]
  public async Task MapDataType_Uuid_ReturnsTextAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.UUID);

    // Assert
    await Assert.That(result).IsEqualTo("TEXT");
  }

  [Test]
  public async Task MapDataType_String_ReturnsTextAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.STRING);

    // Assert
    await Assert.That(result).IsEqualTo("TEXT");
  }

  [Test]
  public async Task MapDataType_StringWithMaxLength_ReturnsTextAsync() {
    // Arrange & Act
    // SQLite doesn't enforce length constraints, so VARCHAR(n) becomes TEXT
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.STRING, maxLength: 255);

    // Assert
    await Assert.That(result).IsEqualTo("TEXT");
  }

  [Test]
  public async Task MapDataType_TimestampTz_ReturnsTextAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.TIMESTAMP_TZ);

    // Assert
    await Assert.That(result).IsEqualTo("TEXT");
  }

  [Test]
  public async Task MapDataType_Json_ReturnsTextAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.JSON);

    // Assert
    await Assert.That(result).IsEqualTo("TEXT");
  }

  [Test]
  public async Task MapDataType_BigInt_ReturnsIntegerAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.BIG_INT);

    // Assert
    await Assert.That(result).IsEqualTo("INTEGER");
  }

  [Test]
  public async Task MapDataType_Integer_ReturnsIntegerAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.INTEGER);

    // Assert
    await Assert.That(result).IsEqualTo("INTEGER");
  }

  [Test]
  public async Task MapDataType_Boolean_ReturnsIntegerAsync() {
    // Arrange & Act
    var result = SqliteTypeMapper.MapDataType(WhizbangDataType.BOOLEAN);

    // Assert
    await Assert.That(result).IsEqualTo("INTEGER");
  }

  [Test]
  public async Task MapDefaultValue_FunctionDateTimeNow_ReturnsCurrentTimestampAsync() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW);

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("CURRENT_TIMESTAMP");
  }

  [Test]
  public async Task MapDefaultValue_FunctionDateTimeUtcNow_ReturnsDatetimeUtcAsync() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.DATE_TIME__UTC_NOW);

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("(datetime('now', 'utc'))");
  }

  [Test]
  public async Task MapDefaultValue_FunctionUuidGenerate_ReturnsLowerHexAsync() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.UUID__GENERATE);

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    // SQLite stores UUIDs as TEXT, application must generate
    await Assert.That(result).IsEqualTo("(lower(hex(randomblob(16))))");
  }

  [Test]
  public async Task MapDefaultValue_FunctionBooleanTrue_Returns1Async() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.BOOLEAN__TRUE);

    // Act
    var result = SqliteTypeMapper.MapDefaultValue(defaultValue);

    // Assert
    await Assert.That(result).IsEqualTo("1");
  }

  [Test]
  public async Task MapDefaultValue_FunctionBooleanFalse_Returns0Async() {
    // Arrange
    var defaultValue = DefaultValue.Function(DefaultValueFunction.BOOLEAN__FALSE);

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

  // --- Unknown-input guards -------------------------------------------------
  // Mirrors PostgresTypeMapperTests: each switch ends in a throwing default arm, the
  // contract for an enum value (or DefaultValue subtype) added without a SQLite mapping.

  /// <summary>A DefaultValue subtype the mapper has no arm for.</summary>
  private sealed record UnmappedDefault : Whizbang.Data.Schema.DefaultValue;

  [Test]
  public async Task MapDataType_UnknownDataType_ThrowsArgumentOutOfRangeAsync() {
    var unknown = (Whizbang.Data.Schema.WhizbangDataType)9999;

    await Assert.That(() => SqliteTypeMapper.MapDataType(unknown))
        .ThrowsExactly<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task MapDefaultValue_UnknownSubtype_ThrowsArgumentOutOfRangeAsync() {
    await Assert.That(() => SqliteTypeMapper.MapDefaultValue(new UnmappedDefault()))
        .ThrowsExactly<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task MapDefaultValue_UnknownFunction_ThrowsArgumentOutOfRangeAsync() {
    var unknown = Whizbang.Data.Schema.DefaultValue.Function(
        (Whizbang.Data.Schema.DefaultValueFunction)9999);

    await Assert.That(() => SqliteTypeMapper.MapDefaultValue(unknown))
        .ThrowsExactly<ArgumentOutOfRangeException>();
  }
}
