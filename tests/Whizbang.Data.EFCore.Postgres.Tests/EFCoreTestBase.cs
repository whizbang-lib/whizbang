using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Base class for EF Core PostgreSQL integration tests using Testcontainers.
/// Each test gets its own isolated PostgreSQL container for maximum isolation and parallel execution.
/// </summary>
public abstract class EFCoreTestBase : IAsyncDisposable {
  static EFCoreTestBase() {
    // Configure Npgsql to use DateTimeOffset for TIMESTAMPTZ columns globally
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private PostgreSqlContainer? _postgresContainer;

  protected string ConnectionString { get; private set; } = null!;
  protected DbContextOptions<WorkCoordinationDbContext> DbContextOptions { get; private set; } = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    var setupSucceeded = false;
    try {
      // Create fresh container for THIS test
      _postgresContainer = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("whizbang_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

      await _postgresContainer.StartAsync();

      // Create connection string with DateTimeOffset support
      var baseConnectionString = _postgresContainer.GetConnectionString();
      // Add Timezone=UTC to ensure TIMESTAMPTZ columns map to DateTimeOffset
      // Add Include Error Detail=true to see detailed error messages for debugging
      ConnectionString = $"{baseConnectionString};Timezone=UTC;Include Error Detail=true";

      // Configure DbContext options
      var optionsBuilder = new DbContextOptionsBuilder<WorkCoordinationDbContext>();
      optionsBuilder.UseNpgsql(ConnectionString);
      DbContextOptions = optionsBuilder.Options;

      // Initialize database schema
      await InitializeDatabaseAsync();

      setupSucceeded = true;
    } finally {
      // Ensure container is cleaned up if setup fails
      if (!setupSucceeded) {
        await TeardownAsync();
      }
    }
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_postgresContainer != null) {
      await _postgresContainer.StopAsync();
      await _postgresContainer.DisposeAsync();
      _postgresContainer = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private async Task InitializeDatabaseAsync() {
    // Use generated EnsureWhizbangDatabaseInitializedAsync extension method
    // This creates all tables, functions, and sequences needed by the EF Core implementation
    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();
  }

  /// <summary>
  /// Creates a new DbContext instance for the current test.
  /// </summary>
  protected WorkCoordinationDbContext CreateDbContext() {
    return new WorkCoordinationDbContext(DbContextOptions);
  }

  /// <summary>
  /// Creates a test message envelope for integration tests.
  /// </summary>
  protected static TestMessageEnvelope CreateTestEnvelope(Guid messageId) {
    return new TestMessageEnvelope {
      MessageId = MessageId.From(messageId),
      Hops = [],
      Payload = JsonDocument.Parse("{}").RootElement  // Empty JSON object for testing
    };
  }

  /// <summary>
  /// Creates an OutboxMessage for testing with proper envelope structure.
  /// </summary>
  protected static OutboxMessage CreateTestOutboxMessage(Guid messageId, string destination, Guid? streamId = null, bool isEvent = false) {
    return new OutboxMessage {
      MessageId = messageId,
      Destination = destination,
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = "TestMessage, TestAssembly",
      StreamId = streamId,
      IsEvent = isEvent
    };
  }

  /// <summary>
  /// Simple test message envelope for integration tests.
  /// Uses JsonElement for AOT-compatible, type-safe serialization.
  /// </summary>
  protected class TestMessageEnvelope : IMessageEnvelope<JsonElement> {
    public required MessageId MessageId { get; init; }
    public required List<MessageHop> Hops { get; init; }
    public required JsonElement Payload { get; init; }

    // Explicit implementation of base interface Payload property
    object IMessageEnvelope.Payload => Payload;

    public void AddHop(MessageHop hop) {
      Hops.Add(hop);
    }

    public DateTimeOffset GetMessageTimestamp() => DateTimeOffset.UtcNow;
    public CorrelationId? GetCorrelationId() => null;
    public MessageId? GetCausationId() => null;
    public JsonElement? GetMetadata(string key) => null;
  }
}
