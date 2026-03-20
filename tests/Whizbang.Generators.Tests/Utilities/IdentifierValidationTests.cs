extern alias shared;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using IDbProviderLimits = shared::Whizbang.Generators.Shared.Limits.IDbProviderLimits;
using IdentifierValidation = shared::Whizbang.Generators.Shared.Utilities.IdentifierValidation;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// Unit tests for IdentifierValidation.
/// Tests database identifier length validation for tables, columns, and indexes.
/// </summary>
public class IdentifierValidationTests {
  /// <summary>
  /// Test implementation of IDbProviderLimits with configurable limits.
  /// </summary>
  private sealed class TestLimits : IDbProviderLimits {
    public int MaxTableNameBytes { get; init; } = 63;
    public int MaxColumnNameBytes { get; init; } = 63;
    public int MaxIndexNameBytes { get; init; } = 63;
    public string ProviderName => "TestProvider";
  }

  #region ValidateTableName Tests

  [Test]
  public async Task ValidateTableName_WithinLimit_ReturnsNullAsync() {
    // Arrange
    const string tableName = "wh_per_order";
    var limits = new TestLimits { MaxTableNameBytes = 63 };

    // Act
    var result = IdentifierValidation.ValidateTableName(tableName, limits);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ValidateTableName_ExceedsLimit_ReturnsErrorAsync() {
    // Arrange - 67 bytes total
    var longName = "wh_per_" + new string('a', 60);
    var limits = new TestLimits { MaxTableNameBytes = 63 };

    // Act
    var result = IdentifierValidation.ValidateTableName(longName, limits);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).Contains("67 bytes");
    await Assert.That(result).Contains("63 bytes");
    await Assert.That(result).Contains("TestProvider");
  }

  [Test]
  public async Task ValidateTableName_ExactlyAtLimit_ReturnsNullAsync() {
    // Arrange - 63 bytes exactly
    var exactName = "wh_per_" + new string('a', 56);
    var limits = new TestLimits { MaxTableNameBytes = 63 };

    // Act
    var result = IdentifierValidation.ValidateTableName(exactName, limits);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ValidateTableName_WithUnicode_CountsBytesCorrectlyAsync() {
    // Arrange - Unicode characters take more bytes (2 bytes each for these)
    // "wh_per_" (7 bytes) + 30 unicode chars (60 bytes) = 67 bytes
    var unicodeName = "wh_per_" + new string('\u00E9', 30);
    var limits = new TestLimits { MaxTableNameBytes = 63 };

    // Act
    var result = IdentifierValidation.ValidateTableName(unicodeName, limits);

    // Assert - Should exceed because UTF-8 encoding uses 2 bytes per char
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task ValidateTableName_EmptyString_ReturnsNullAsync() {
    // Arrange
    var limits = new TestLimits { MaxTableNameBytes = 63 };

    // Act
    var result = IdentifierValidation.ValidateTableName("", limits);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ValidateTableName_NullString_ReturnsNullAsync() {
    // Arrange
    var limits = new TestLimits { MaxTableNameBytes = 63 };

    // Act
    var result = IdentifierValidation.ValidateTableName(null!, limits);

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region ValidateColumnName Tests

  [Test]
  public async Task ValidateColumnName_WithinLimit_ReturnsNullAsync() {
    // Arrange
    const string columnName = "order_id";
    var limits = new TestLimits { MaxColumnNameBytes = 63 };

    // Act
    var result = IdentifierValidation.ValidateColumnName(columnName, limits);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ValidateColumnName_ExceedsLimit_ReturnsErrorAsync() {
    // Arrange - Create a column name that exceeds 63 bytes
    var longColumnName = new string('x', 70);
    var limits = new TestLimits { MaxColumnNameBytes = 63 };

    // Act
    var result = IdentifierValidation.ValidateColumnName(longColumnName, limits);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).Contains("70 bytes");
    await Assert.That(result).Contains("Column name");
  }

  [Test]
  public async Task ValidateColumnName_EmptyString_ReturnsNullAsync() {
    // Arrange
    var limits = new TestLimits { MaxColumnNameBytes = 63 };

    // Act
    var result = IdentifierValidation.ValidateColumnName("", limits);

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region ValidateIndexName Tests

  [Test]
  public async Task ValidateIndexName_WithinLimit_ReturnsNullAsync() {
    // Arrange
    const string indexName = "ix_orders_customer_id";
    var limits = new TestLimits { MaxIndexNameBytes = 63 };

    // Act
    var result = IdentifierValidation.ValidateIndexName(indexName, limits);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ValidateIndexName_ExceedsLimit_ReturnsErrorAsync() {
    // Arrange - Create an index name that exceeds 63 bytes
    var longIndexName = "ix_" + new string('y', 65);
    var limits = new TestLimits { MaxIndexNameBytes = 63 };

    // Act
    var result = IdentifierValidation.ValidateIndexName(longIndexName, limits);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).Contains("68 bytes");
    await Assert.That(result).Contains("Index name");
  }

  [Test]
  public async Task ValidateIndexName_EmptyString_ReturnsNullAsync() {
    // Arrange
    var limits = new TestLimits { MaxIndexNameBytes = 63 };

    // Act
    var result = IdentifierValidation.ValidateIndexName("", limits);

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region GetByteCount Tests

  [Test]
  public async Task GetByteCount_AsciiString_ReturnsLengthAsync() {
    // Arrange - ASCII characters are 1 byte each
    const string ascii = "hello_world";

    // Act
    var result = IdentifierValidation.GetByteCount(ascii);

    // Assert
    await Assert.That(result).IsEqualTo(11);
  }

  [Test]
  public async Task GetByteCount_UnicodeString_ReturnsCorrectBytesAsync() {
    // Arrange - '\u00E9' (e with accent) is 2 bytes in UTF-8
    const string unicode = "caf\u00E9";

    // Act
    var result = IdentifierValidation.GetByteCount(unicode);

    // Assert - 3 ascii bytes + 2 bytes for \u00E9 = 5 bytes
    await Assert.That(result).IsEqualTo(5);
  }

  [Test]
  public async Task GetByteCount_EmptyString_ReturnsZeroAsync() {
    // Act
    var result = IdentifierValidation.GetByteCount("");

    // Assert
    await Assert.That(result).IsEqualTo(0);
  }

  [Test]
  public async Task GetByteCount_NullString_ReturnsZeroAsync() {
    // Act
    var result = IdentifierValidation.GetByteCount(null!);

    // Assert
    await Assert.That(result).IsEqualTo(0);
  }

  #endregion

  #region IsTableNameValid Tests

  [Test]
  public async Task IsTableNameValid_WithinLimit_ReturnsTrueAsync() {
    // Arrange
    var limits = new TestLimits { MaxTableNameBytes = 63 };

    // Act
    var result = IdentifierValidation.IsTableNameValid("wh_per_order", limits);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsTableNameValid_ExceedsLimit_ReturnsFalseAsync() {
    // Arrange
    var limits = new TestLimits { MaxTableNameBytes = 63 };
    var longName = new string('x', 70);

    // Act
    var result = IdentifierValidation.IsTableNameValid(longName, limits);

    // Assert
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region IsColumnNameValid Tests

  [Test]
  public async Task IsColumnNameValid_WithinLimit_ReturnsTrueAsync() {
    // Arrange
    var limits = new TestLimits { MaxColumnNameBytes = 63 };

    // Act
    var result = IdentifierValidation.IsColumnNameValid("order_id", limits);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsColumnNameValid_ExceedsLimit_ReturnsFalseAsync() {
    // Arrange
    var limits = new TestLimits { MaxColumnNameBytes = 63 };
    var longName = new string('x', 70);

    // Act
    var result = IdentifierValidation.IsColumnNameValid(longName, limits);

    // Assert
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region IsIndexNameValid Tests

  [Test]
  public async Task IsIndexNameValid_WithinLimit_ReturnsTrueAsync() {
    // Arrange
    var limits = new TestLimits { MaxIndexNameBytes = 63 };

    // Act
    var result = IdentifierValidation.IsIndexNameValid("ix_orders_id", limits);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsIndexNameValid_ExceedsLimit_ReturnsFalseAsync() {
    // Arrange
    var limits = new TestLimits { MaxIndexNameBytes = 63 };
    var longName = new string('x', 70);

    // Act
    var result = IdentifierValidation.IsIndexNameValid(longName, limits);

    // Assert
    await Assert.That(result).IsFalse();
  }

  #endregion
}
