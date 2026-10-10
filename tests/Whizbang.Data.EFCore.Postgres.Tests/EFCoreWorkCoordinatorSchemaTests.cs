// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests for EFCoreWorkCoordinator.GetSchemaWithFallback internal method.
/// Tests all branches for 100% line and branch coverage:
/// - Valid non-empty schema returns that schema
/// - Null schema logs warning and returns default
/// - Empty schema logs warning and returns default
/// And ResolveSchema, through which every coordinator call resolves its schema from the model.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard2")]
public class EFCoreWorkCoordinatorSchemaTests {
  private const string DEFAULT_SCHEMA = "public";

  [Test]
  public async Task GetSchemaWithFallback_WhenSchemaIsValid_ReturnsSchemaAsync() {
    // Arrange
    const string expectedSchema = "my_custom_schema";
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
    const string? schema = null;
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
    const string? schema = "   ";
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
    const string? schema = null;

    // Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.GetSchemaWithFallback(
      schema,
      DEFAULT_SCHEMA,
      logger: null);

    // Assert
    await Assert.That(result).IsEqualTo(DEFAULT_SCHEMA);
  }

  // ============================================================
  // ResolveSchema tests - the model decides the schema, once for every call site
  // ============================================================

  [Test]
  public async Task ResolveSchema_WhenModelMapsEntityToSchema_ReturnsThatSchemaAsync() {
    using var context = SchemaProbeDbContext.Create();
    var logger = new CapturingLogger();

    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.ResolveSchema(
      context.Model, typeof(SchemaProbeRow), logger);

    await Assert.That(result).IsEqualTo(SchemaProbeDbContext.PROBE_SCHEMA);
    await Assert.That(logger.WarningCount).IsEqualTo(0)
      .Because("a mapped schema is used as-is, with nothing to warn about");
  }

  [Test]
  public async Task ResolveSchema_WhenModelDoesNotMapEntity_LogsWarningAndReturnsDefaultAsync() {
    // The probe model maps no OutboxRecord, which is the "entity not in the model" outcome every
    // coordinator call shares: the SQL then runs in the default schema rather than failing.
    using var context = SchemaProbeDbContext.Create();
    var logger = new CapturingLogger();

    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.ResolveSchema(
      context.Model, typeof(OutboxRecord), logger);

    await Assert.That(context.Model.FindEntityType(typeof(OutboxRecord))).IsNull();
    await Assert.That(result).IsEqualTo(DEFAULT_SCHEMA);
    await Assert.That(logger.WarningCount).IsEqualTo(1);
    await Assert.That(logger.LastWarningMessage).Contains("falling back");
  }

  [Test]
  public async Task ResolveSchema_WhenModelIsNull_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => EFCoreWorkCoordinator<WorkCoordinationDbContext>.ResolveSchema(
        null!, typeof(OutboxRecord), logger: null))
      .Throws<ArgumentNullException>();
  }

  // ============================================================
  // BuildSchemaQualifiedName tests - CRITICAL: Never produce leading dot
  // ============================================================

  [Test]
  public async Task BuildSchemaQualifiedName_WhenSchemaIsPublic_ReturnsUnqualifiedNameAsync() {
    // Arrange & Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.BuildSchemaQualifiedName(
      "public",
      "claim_work");

    // Assert - Should NOT have schema prefix for public
    await Assert.That(result).IsEqualTo("claim_work");
    await Assert.That(result).DoesNotStartWith(".");
  }

  [Test]
  public async Task BuildSchemaQualifiedName_WhenSchemaIsEmpty_ReturnsUnqualifiedNameAsync() {
    // Arrange & Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.BuildSchemaQualifiedName(
      "",
      "claim_work");

    // Assert - Should NOT have leading dot
    await Assert.That(result).IsEqualTo("claim_work");
    await Assert.That(result).DoesNotStartWith(".");
  }

  [Test]
  public async Task BuildSchemaQualifiedName_WhenSchemaIsWhitespace_ReturnsUnqualifiedNameAsync() {
    // Arrange & Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.BuildSchemaQualifiedName(
      "   ",
      "claim_work");

    // Assert - Should NOT have leading dot
    await Assert.That(result).IsEqualTo("claim_work");
    await Assert.That(result).DoesNotStartWith(".");
  }

  [Test]
  public async Task BuildSchemaQualifiedName_WhenSchemaIsCustom_ReturnsQuotedSchemaQualifiedNameAsync() {
    // Arrange & Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.BuildSchemaQualifiedName(
      "inventory",
      "claim_work");

    // Assert - Should have quoted schema prefix
    await Assert.That(result).IsEqualTo("\"inventory\".claim_work");
    await Assert.That(result).DoesNotStartWith(".");
  }

  [Test]
  public async Task BuildSchemaQualifiedName_WhenSchemaIsReservedWord_ReturnsQuotedSchemaAsync() {
    // Arrange - "user" is a PostgreSQL reserved word
    // Act
    var result = EFCoreWorkCoordinator<WorkCoordinationDbContext>.BuildSchemaQualifiedName(
      "user",
      "complete_perspective_cursor_work");

    // Assert - Should have quoted schema to handle reserved word
    await Assert.That(result).IsEqualTo("\"user\".complete_perspective_cursor_work");
    await Assert.That(result).DoesNotStartWith(".");
  }

  public sealed class SchemaProbeRow {
    public int Id { get; set; }
  }

  /// <summary>A model with one entity in a custom schema and no Whizbang entities.</summary>
  private sealed class SchemaProbeDbContext(DbContextOptions<SchemaProbeDbContext> options) : DbContext(options) {
    public const string PROBE_SCHEMA = "inventory";

    // Building the model needs a provider, not a server: nothing here opens a connection.
    public static SchemaProbeDbContext Create() => new(new DbContextOptionsBuilder<SchemaProbeDbContext>()
      .UseNpgsql("Host=localhost;Database=probe;Username=u;Password=p").Options);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<SchemaProbeRow>().ToTable("schema_probe", PROBE_SCHEMA);
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
