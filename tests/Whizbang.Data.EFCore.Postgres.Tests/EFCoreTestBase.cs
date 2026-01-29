using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Base class for EF Core PostgreSQL integration tests using Testcontainers.
/// Uses a shared PostgreSQL container with per-test database isolation.
/// This approach avoids the previous issue where each test created its own container,
/// causing 60+ simultaneous container startups and Docker resource exhaustion.
/// </summary>
public abstract class EFCoreTestBase : IAsyncDisposable {
  static EFCoreTestBase() {
    // Configure Npgsql to use DateTimeOffset for TIMESTAMPTZ columns globally
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private string? _testDatabaseName;
  private NpgsqlDataSource? _dataSource;

  protected string ConnectionString { get; private set; } = null!;
  protected DbContextOptions<WorkCoordinationDbContext> DbContextOptions { get; private set; } = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    var setupSucceeded = false;
    try {
      // Initialize shared container (only starts once, subsequent calls return immediately)
      await SharedPostgresContainer.InitializeAsync();

      // Create unique database for THIS test
      _testDatabaseName = $"test_{Guid.NewGuid():N}";

      // Connect to main database to create the test database
      await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await adminConnection.OpenAsync();
      await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

      // Build connection string for the test database
      var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
        Database = _testDatabaseName,
        // Add Timezone=UTC to ensure TIMESTAMPTZ columns map to DateTimeOffset
        Timezone = "UTC",
        // Add Include Error Detail=true to see detailed error messages for debugging
        IncludeErrorDetail = true
      };
      ConnectionString = builder.ConnectionString;

      // Configure Npgsql data source with JSON serializer options
      var dataSourceBuilder = new NpgsqlDataSourceBuilder(ConnectionString);

      // CRITICAL CONFIGURATION FOR POCO JSON MAPPING:
      // All entities (infrastructure + perspectives) now use custom POCO types with .HasColumnType("jsonb")
      // - Infrastructure: InboxMessageData, OutboxMessageData, EnvelopeMetadata, MessageScope, ServiceInstanceMetadata
      // - Perspectives: PerspectiveRow<T>.Data, Metadata, Scope
      // - Test types: Order, SampleOrderCreatedEvent (registered via TestJsonContext)
      //
      // JsonContextRegistry.CreateCombinedOptions() provides source-generated converters for ALL registered contexts:
      // - Whizbang.Core contexts (WhizbangIdJsonContext, InfrastructureJsonContext, MessageJsonContext)
      // - Test contexts (TestJsonContext with Order, etc.)
      // EnableDynamicJson() enables POCO → JSONB mapping for properties with .HasColumnType("jsonb")
      //
      // IMPORTANT: ConfigureJsonOptions() MUST be called BEFORE EnableDynamicJson() (Npgsql bug #5562)
      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      dataSourceBuilder.ConfigureJsonOptions(jsonOptions);
      dataSourceBuilder.EnableDynamicJson();

      _dataSource = dataSourceBuilder.Build();

      // Configure DbContext options to use the data source
      var optionsBuilder = new DbContextOptionsBuilder<WorkCoordinationDbContext>();
      optionsBuilder.UseNpgsql(_dataSource, npgsqlOptions => {
        // Register Whizbang's custom PostgreSQL function translators
        // This enables optimized ?| array overlap for large principal sets
        npgsqlOptions.UseWhizbangFunctions();
      });
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
    if (_dataSource != null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }

    // Drop the test-specific database to clean up
    if (_testDatabaseName != null) {
      try {
        // Close all connections to the test database first
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();

        // Terminate connections to the test database
        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pg_stat_activity.pid)
          FROM pg_stat_activity
          WHERE pg_stat_activity.datname = '{_testDatabaseName}'
          AND pid <> pg_backend_pid()");

        // Drop the database
        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
      } catch {
        // Ignore cleanup errors - the database will be cleaned up when the container stops
      }

      _testDatabaseName = null;
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
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      },
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
