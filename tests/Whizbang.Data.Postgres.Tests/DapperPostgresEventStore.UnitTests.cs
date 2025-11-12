using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Unit tests for DapperPostgresEventStore internal methods.
/// Tests the string-based fallback path for unique constraint detection.
/// PostgresException branches are covered by integration/retry tests.
/// </summary>
public class DapperPostgresEventStoreUnitTests {
  [Test]
  public async Task IsUniqueConstraintViolation_WithNonPostgresException_UniqueConstraintMessage_ShouldReturnTrueAsync() {
    // Arrange
    var ex = new Exception("Error: unique constraint violation occurred");

    // Act
    var result = DapperPostgresEventStore.IsUniqueConstraintViolation(ex);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsUniqueConstraintViolation_WithNonPostgresException_DuplicateKeyMessage_ShouldReturnTrueAsync() {
    // Arrange
    var ex = new Exception("Error: duplicate key value violates constraint");

    // Act
    var result = DapperPostgresEventStore.IsUniqueConstraintViolation(ex);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsUniqueConstraintViolation_WithNonPostgresException_CaseInsensitive_ShouldReturnTrueAsync() {
    // Arrange
    var ex = new Exception("ERROR: UNIQUE CONSTRAINT VIOLATION");

    // Act
    var result = DapperPostgresEventStore.IsUniqueConstraintViolation(ex);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsUniqueConstraintViolation_WithNonPostgresException_DifferentMessage_ShouldReturnFalseAsync() {
    // Arrange
    var ex = new Exception("Some other database error");

    // Act
    var result = DapperPostgresEventStore.IsUniqueConstraintViolation(ex);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsUniqueConstraintViolation_WithArgumentException_DifferentMessage_ShouldReturnFalseAsync() {
    // Arrange
    var ex = new ArgumentException("Invalid argument provided");

    // Act
    var result = DapperPostgresEventStore.IsUniqueConstraintViolation(ex);

    // Assert
    await Assert.That(result).IsFalse();
  }
}
