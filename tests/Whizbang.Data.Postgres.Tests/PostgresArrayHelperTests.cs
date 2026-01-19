using NpgsqlTypes;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Tests for PostgresArrayHelper methods.
/// Validates type-safe array parameter creation for PostgreSQL.
/// </summary>
public class PostgresArrayHelperTests {
  /// <summary>
  /// Test: ToUuidArray with valid Guid array creates UUID[] parameter
  /// </summary>
  [Test]
  public async Task ToUuidArray_ValidGuidArray_CreatesUuidArrayParameterAsync() {
    // Arrange
    var guids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

    // Act
    var param = PostgresArrayHelper.ToUuidArray(guids);

    // Assert
    await Assert.That(param.Value).IsEqualTo(guids);
    await Assert.That(param.NpgsqlDbType).IsEqualTo(NpgsqlDbType.Array | NpgsqlDbType.Uuid);
  }

  /// <summary>
  /// Test: ToUuidArray with null input creates empty UUID[] parameter
  /// </summary>
  [Test]
  public async Task ToUuidArray_NullInput_CreatesEmptyArrayParameterAsync() {
    // Arrange
    Guid[]? guids = null;

    // Act
    var param = PostgresArrayHelper.ToUuidArray(guids);

    // Assert
    await Assert.That(param.Value).IsEqualTo(Array.Empty<Guid>());
    await Assert.That(param.NpgsqlDbType).IsEqualTo(NpgsqlDbType.Array | NpgsqlDbType.Uuid);
  }

  /// <summary>
  /// Test: ToVarcharArray with valid string array creates VARCHAR[] parameter
  /// </summary>
  [Test]
  public async Task ToVarcharArray_ValidStringArray_CreatesVarcharArrayParameterAsync() {
    // Arrange
    var strings = new[] { "foo", "bar", "baz" };

    // Act
    var param = PostgresArrayHelper.ToVarcharArray(strings);

    // Assert
    await Assert.That(param.Value).IsEqualTo(strings);
    await Assert.That(param.NpgsqlDbType).IsEqualTo(NpgsqlDbType.Array | NpgsqlDbType.Varchar);
  }

  /// <summary>
  /// Test: ToVarcharArray with null input creates empty VARCHAR[] parameter
  /// </summary>
  [Test]
  public async Task ToVarcharArray_NullInput_CreatesEmptyArrayParameterAsync() {
    // Arrange
    string[]? strings = null;

    // Act
    var param = PostgresArrayHelper.ToVarcharArray(strings);

    // Assert
    await Assert.That(param.Value).IsEqualTo(Array.Empty<string>());
    await Assert.That(param.NpgsqlDbType).IsEqualTo(NpgsqlDbType.Array | NpgsqlDbType.Varchar);
  }

  /// <summary>
  /// Test: ToIntegerArray with valid int array creates INTEGER[] parameter
  /// </summary>
  [Test]
  public async Task ToIntegerArray_ValidIntArray_CreatesIntegerArrayParameterAsync() {
    // Arrange
    var integers = new[] { 1, 2, 3, 42, 100 };

    // Act
    var param = PostgresArrayHelper.ToIntegerArray(integers);

    // Assert
    await Assert.That(param.Value).IsEqualTo(integers);
    await Assert.That(param.NpgsqlDbType).IsEqualTo(NpgsqlDbType.Array | NpgsqlDbType.Integer);
  }

  /// <summary>
  /// Test: ToIntegerArray with null input creates empty INTEGER[] parameter
  /// </summary>
  [Test]
  public async Task ToIntegerArray_NullInput_CreatesEmptyArrayParameterAsync() {
    // Arrange
    int[]? integers = null;

    // Act
    var param = PostgresArrayHelper.ToIntegerArray(integers);

    // Assert
    await Assert.That(param.Value).IsEqualTo(Array.Empty<int>());
    await Assert.That(param.NpgsqlDbType).IsEqualTo(NpgsqlDbType.Array | NpgsqlDbType.Integer);
  }

  /// <summary>
  /// Test: EmptyUuidArray creates empty UUID[] parameter
  /// </summary>
  [Test]
  public async Task EmptyUuidArray_CreatesEmptyUuidArrayParameterAsync() {
    // Arrange & Act
    var param = PostgresArrayHelper.EmptyUuidArray();

    // Assert
    await Assert.That(param.Value).IsEqualTo(Array.Empty<Guid>());
    await Assert.That(param.NpgsqlDbType).IsEqualTo(NpgsqlDbType.Array | NpgsqlDbType.Uuid);
  }
}
