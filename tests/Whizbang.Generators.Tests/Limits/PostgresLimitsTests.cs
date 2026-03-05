extern alias postgres_generators;

using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using PostgresLimits = postgres_generators::Whizbang.Data.EFCore.Postgres.Generators.Limits.PostgresLimits;

namespace Whizbang.Generators.Tests.Limits;

/// <summary>
/// Unit tests for PostgresLimits.
/// Tests PostgreSQL-specific identifier limits.
/// </summary>
public class PostgresLimitsTests {
  #region Constant Tests

  [Test]
  public async Task MAX_IDENTIFIER_BYTES_Is63Async() {
    // Arrange - store constant in variable to satisfy TUnit assertion rules
    var value = PostgresLimits.MAX_IDENTIFIER_BYTES;

    // Assert - PostgreSQL NAMEDATALEN - 1 = 63
    await Assert.That(value).IsEqualTo(63);
  }

  #endregion

  #region Instance Property Tests

  [Test]
  public async Task Instance_IsNotNullAsync() {
    // Act
    var instance = PostgresLimits.Instance;

    // Assert
    await Assert.That(instance).IsNotNull();
  }

  [Test]
  public async Task Instance_IsSingletonAsync() {
    // Act
    var instance1 = PostgresLimits.Instance;
    var instance2 = PostgresLimits.Instance;

    // Assert - Same reference
    await Assert.That(ReferenceEquals(instance1, instance2)).IsTrue();
  }

  [Test]
  public async Task MaxTableNameBytes_Returns63Async() {
    // Arrange
    var limits = PostgresLimits.Instance;

    // Act
    var result = limits.MaxTableNameBytes;

    // Assert
    await Assert.That(result).IsEqualTo(63);
  }

  [Test]
  public async Task MaxColumnNameBytes_Returns63Async() {
    // Arrange
    var limits = PostgresLimits.Instance;

    // Act
    var result = limits.MaxColumnNameBytes;

    // Assert
    await Assert.That(result).IsEqualTo(63);
  }

  [Test]
  public async Task MaxIndexNameBytes_Returns63Async() {
    // Arrange
    var limits = PostgresLimits.Instance;

    // Act
    var result = limits.MaxIndexNameBytes;

    // Assert
    await Assert.That(result).IsEqualTo(63);
  }

  [Test]
  public async Task ProviderName_ReturnsPostgreSQLAsync() {
    // Arrange
    var limits = PostgresLimits.Instance;

    // Act
    var result = limits.ProviderName;

    // Assert
    await Assert.That(result).IsEqualTo("PostgreSQL");
  }

  #endregion

  #region Interface Implementation Tests

  [Test]
  public async Task ImplementsIDbProviderLimitsAsync() {
    // Arrange
    var limits = PostgresLimits.Instance;
    var limitsType = limits.GetType();

    // Assert - Should implement IDbProviderLimits interface (using reflection due to ILRepack internalization)
    var interfaces = limitsType.GetInterfaces();
    var hasInterface = interfaces.Any(i => i.Name == "IDbProviderLimits");
    await Assert.That(hasInterface).IsTrue();
  }

  [Test]
  public async Task AllLimitsMatch_MAX_IDENTIFIER_BYTES_ConstantAsync() {
    // Arrange
    var limits = PostgresLimits.Instance;

    // Assert - All limits should match the constant
    await Assert.That(limits.MaxTableNameBytes).IsEqualTo(PostgresLimits.MAX_IDENTIFIER_BYTES);
    await Assert.That(limits.MaxColumnNameBytes).IsEqualTo(PostgresLimits.MAX_IDENTIFIER_BYTES);
    await Assert.That(limits.MaxIndexNameBytes).IsEqualTo(PostgresLimits.MAX_IDENTIFIER_BYTES);
  }

  #endregion
}
