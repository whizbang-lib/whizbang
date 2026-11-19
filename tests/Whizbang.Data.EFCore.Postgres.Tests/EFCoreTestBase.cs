// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Base class for EF Core PostgreSQL integration tests using Testcontainers.
/// Uses a shared PostgreSQL container with per-test database isolation.
/// This approach avoids the previous issue where each test created its own container,
/// causing 60+ simultaneous container startups and Docker resource exhaustion.
/// </summary>
/// <remarks>
/// <para>
/// Parallel by default (#2069). Every test gets a database of its own, so the shared container is not shared state:
/// what bounds how many run at once is <see cref="EFCorePostgresParallelism"/>, sized to the container's connections.
/// A test class serializes only on what it really shares with other tests (a process-wide static, a registry it
/// resets, a cluster-wide object), with a key named for that resource and a comment saying why; see
/// <c>ai-docs/efcore-test-parallelism.md</c> for the classification and <c>TestIsolation/ParallelismPolicyGuardTests</c>
/// for the rules a build enforces.
/// </para>
/// <para>
/// The database is created and dropped through <see cref="PerTestDatabaseFactory"/>: with many tests creating
/// databases at once, a <c>CREATE DATABASE</c> can lose the race for the template (55006), which the factory retries.
/// </para>
/// </remarks>
public abstract class EFCoreTestBase : IAsyncDisposable {
  static EFCoreTestBase() {
    // Configure Npgsql to use DateTimeOffset for TIMESTAMPTZ columns globally
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  /// <summary>Names the scratch databases this base creates, so a leftover one is traceable to it.</summary>
  private const string DATABASE_PREFIX = "ef";

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

      // A database of this test's own, copied from the run's migrated template: already the full schema.
      var database = await EFCoreSchemaTemplate.CreateDatabaseAsync(DATABASE_PREFIX);
      _testDatabaseName = database.Name;

      // Build connection string for the test database
      var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString) {
        // Add Timezone=UTC to ensure TIMESTAMPTZ columns map to DateTimeOffset
        Timezone = "UTC",
        // Add Include Error Detail=true to see detailed error messages for debugging
        IncludeErrorDetail = true
      };
      ConnectionString = builder.ConnectionString;

      (_dataSource, var options) = CreateContextOptions(ConnectionString);
      DbContextOptions = options;

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
      // Return this database's pooled sessions before asking PostgreSQL to drop it. Npgsql pools per connection
      // string, and a pooled session that reconnects in the gap between terminating backends and issuing the DROP
      // is enough to make the DROP fail. Clearing the pool removes the reconnect.
      if (!string.IsNullOrEmpty(ConnectionString)) {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString));
      }

      // DropAsync uses WITH (FORCE), retries the contention a busy shared container produces, and abandons rather
      // than throws, because a teardown failure must not mask the result of the test that just ran. Dropping
      // matters beyond disk: a shared container once held 71 leaked test databases after two hours, the suite
      // slowed from 6m52s to 16m37s as they accumulated, and their open transactions held the cluster's cleanup
      // horizon back (#671).
      await PerTestDatabaseFactory.DropAsync(_testDatabaseName);
      _testDatabaseName = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// The data source and context options every test of this base, and the schema template it copies, reach their
  /// database through.
  /// </summary>
  /// <param name="connectionString">The connection string to the database.</param>
  /// <returns>The data source, which the caller disposes, and options built over it.</returns>
  /// <remarks>
  /// One place, so the template is migrated through exactly the JSON, vector and function configuration a test's
  /// context has: a template built under different options could record a different schema than a test would.
  /// <c>ConfigureJsonOptions</c> comes before <c>EnableDynamicJson</c> (Npgsql #5562); the combined options carry the
  /// source-generated converters of every registered context, the framework's and the tests'.
  /// </remarks>
  internal static (NpgsqlDataSource DataSource, DbContextOptions<WorkCoordinationDbContext> Options) CreateContextOptions(
      string connectionString) {
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.ConfigureJsonOptions(JsonContextRegistry.CreateCombinedOptions());
    dataSourceBuilder.EnableDynamicJson();
    dataSourceBuilder.UseVector();
    var dataSource = dataSourceBuilder.Build();

    var optionsBuilder = new DbContextOptionsBuilder<WorkCoordinationDbContext>();
    optionsBuilder.UseNpgsql(dataSource, npgsqlOptions => npgsqlOptions.UseWhizbangFunctions())
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    return (dataSource, optionsBuilder.Options);
  }

  /// <summary>
  /// Creates a new DbContext instance for the current test.
  /// </summary>
  protected WorkCoordinationDbContext CreateDbContext() {
    return new WorkCoordinationDbContext(DbContextOptions);
  }

  /// <summary>
  /// Creates a test message envelope for integration tests.
  /// Uses the real MessageEnvelope type to ensure proper JSON serialization with short property names.
  /// </summary>
  protected static MessageEnvelope<JsonElement> CreateTestEnvelope(Guid messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
      Payload = JsonElement.Parse("{}")  // Empty JSON object for testing
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

}
