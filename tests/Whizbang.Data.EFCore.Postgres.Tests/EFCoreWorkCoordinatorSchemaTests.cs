using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests for EFCoreWorkCoordinator.GetSchemaWithFallback internal method.
/// Tests all branches for 100% line and branch coverage:
/// - Valid non-empty schema returns that schema
/// - Null schema logs warning and returns default
/// - Empty schema logs warning and returns default
/// </summary>
public class EFCoreWorkCoordinatorSchemaTests {
  private const string DEFAULT_SCHEMA = "public";

  [Test]
  public async Task GetSchemaWithFallback_WhenSchemaIsValid_ReturnsSchemaAsync() {
    // Arrange
    var expectedSchema = "my_custom_schema";
    var logger = new CapturingLogger();

    // Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.GetSchemaWithFallback(
      expectedSchema,
      DEFAULT_SCHEMA,
      logger);

    // Assert
    await Assert.That(result).IsEqualTo(expectedSchema);
    await Assert.That(logger.WarningCount).IsEqualTo(0)
      .Because("no warning should be logged when schema is valid");
  }

  [Test]
  public async Task GetSchemaWithFallback_WhenSchemaIsNull_LogsWarningAndReturnsDefaultAsync() {
    // Arrange
    string? schema = null;
    var logger = new CapturingLogger();

    // Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.GetSchemaWithFallback(
      schema,
      DEFAULT_SCHEMA,
      logger);

    // Assert
    await Assert.That(result).IsEqualTo(DEFAULT_SCHEMA);
    await Assert.That(logger.WarningCount).IsEqualTo(1);
    await Assert.That(logger.LastWarningMessage).Contains("falling back");
    await Assert.That(logger.LastWarningMessage).Contains(DEFAULT_SCHEMA);
  }

  [Test]
  public async Task GetSchemaWithFallback_WhenSchemaIsEmpty_LogsWarningAndReturnsDefaultAsync() {
    // Arrange
    string? schema = string.Empty;
    var logger = new CapturingLogger();

    // Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.GetSchemaWithFallback(
      schema,
      DEFAULT_SCHEMA,
      logger);

    // Assert
    await Assert.That(result).IsEqualTo(DEFAULT_SCHEMA);
    await Assert.That(logger.WarningCount).IsEqualTo(1);
    await Assert.That(logger.LastWarningMessage).Contains("falling back");
    await Assert.That(logger.LastWarningMessage).Contains(DEFAULT_SCHEMA);
  }

  [Test]
  public async Task GetSchemaWithFallback_WhenSchemaIsWhitespace_LogsWarningAndReturnsDefaultAsync() {
    // Arrange
    string? schema = "   ";
    var logger = new CapturingLogger();

    // Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.GetSchemaWithFallback(
      schema,
      DEFAULT_SCHEMA,
      logger);

    // Assert
    await Assert.That(result).IsEqualTo(DEFAULT_SCHEMA);
    await Assert.That(logger.WarningCount).IsEqualTo(1);
    await Assert.That(logger.LastWarningMessage).Contains("falling back");
  }

  [Test]
  public async Task GetSchemaWithFallback_WhenLoggerIsNull_DoesNotThrowAsync() {
    // Arrange - null logger should not cause exceptions
    string? schema = null;

    // Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.GetSchemaWithFallback(
      schema,
      DEFAULT_SCHEMA,
      logger: null);

    // Assert
    await Assert.That(result).IsEqualTo(DEFAULT_SCHEMA);
  }

  // ============================================================
  // BuildSchemaQualifiedName tests - CRITICAL: Never produce leading dot
  // ============================================================

  [Test]
  public async Task BuildSchemaQualifiedName_WhenSchemaIsPublic_ReturnsUnqualifiedNameAsync() {
    // Arrange & Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.BuildSchemaQualifiedName(
      "public",
      "process_work_batch");

    // Assert - Should NOT have schema prefix for public
    await Assert.That(result).IsEqualTo("process_work_batch");
    await Assert.That(result).DoesNotStartWith(".");
  }

  [Test]
  public async Task BuildSchemaQualifiedName_WhenSchemaIsEmpty_ReturnsUnqualifiedNameAsync() {
    // Arrange & Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.BuildSchemaQualifiedName(
      "",
      "process_work_batch");

    // Assert - Should NOT have leading dot
    await Assert.That(result).IsEqualTo("process_work_batch");
    await Assert.That(result).DoesNotStartWith(".");
  }

  [Test]
  public async Task BuildSchemaQualifiedName_WhenSchemaIsWhitespace_ReturnsUnqualifiedNameAsync() {
    // Arrange & Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.BuildSchemaQualifiedName(
      "   ",
      "process_work_batch");

    // Assert - Should NOT have leading dot
    await Assert.That(result).IsEqualTo("process_work_batch");
    await Assert.That(result).DoesNotStartWith(".");
  }

  [Test]
  public async Task BuildSchemaQualifiedName_WhenSchemaIsCustom_ReturnsQuotedSchemaQualifiedNameAsync() {
    // Arrange & Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.BuildSchemaQualifiedName(
      "inventory",
      "process_work_batch");

    // Assert - Should have quoted schema prefix
    await Assert.That(result).IsEqualTo("\"inventory\".process_work_batch");
    await Assert.That(result).DoesNotStartWith(".");
  }

  [Test]
  public async Task BuildSchemaQualifiedName_WhenSchemaIsReservedWord_ReturnsQuotedSchemaAsync() {
    // Arrange - "user" is a PostgreSQL reserved word
    // Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.BuildSchemaQualifiedName(
      "user",
      "complete_perspective_checkpoint_work");

    // Assert - Should have quoted schema to handle reserved word
    await Assert.That(result).IsEqualTo("\"user\".complete_perspective_checkpoint_work");
    await Assert.That(result).DoesNotStartWith(".");
  }

  /// <summary>
  /// Simple logger that captures log messages for test verification.
  /// More straightforward than mocking ILogger with Rocks due to TState complexity.
  /// </summary>
  private sealed class CapturingLogger : ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>> {
    public int WarningCount { get; private set; }
    public string? LastWarningMessage { get; private set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
      LogLevel logLevel,
      EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        WarningCount++;
        LastWarningMessage = formatter(state, exception);
      }
    }
  }
}
