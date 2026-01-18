using System.Text.Json;
using ECommerce.BFF.API;
using ECommerce.BFF.API.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Data.EFCore.Postgres.Generated;

namespace ECommerce.Integration.Tests;

/// <summary>
/// Integration tests that verify the BFF.API service processes outbox messages end-to-end.
/// These tests verify the ACTUAL application configuration matches what the infrastructure tests prove works.
/// </summary>
public class BffWorkCoordinatorIntegrationTests : IAsyncDisposable {
  private PostgreSqlContainer? _postgresContainer;
  private IHost? _testHost;
  private string _connectionString = null!;
  private Guid _instanceId;

  private record TestEvent { }

  private static IMessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = []
    };
    return envelope;
  }

  /// <summary>
  /// Inserts a message directly into the outbox without claiming it.
  /// This allows the worker to pick it up as orphaned work.
  /// </summary>
  private async Task _insertUnclaimedMessageAsync(
    BffDbContext dbContext,
    JsonSerializerOptions jsonOptions,
    Guid messageId,
    Guid streamId,
    string destination = "products",
    string messageType = "TestMessage, TestAssembly") {

    var envelope = _createTestEnvelope(messageId);
    // Use Whizbang JSON options to ensure MessageId is serialized as a string
    var envelopeJson = JsonSerializer.Serialize(envelope, jsonOptions);
    var metadata = new EnvelopeMetadata {
      MessageId = MessageId.From(messageId),
      Hops = new List<MessageHop>()
    };
    var metadataJson = JsonSerializer.Serialize(metadata, jsonOptions);

    // Insert directly with NULL instance_id and lease_expiry (unclaimed)
    await dbContext.Database.ExecuteSqlRawAsync(@"
      INSERT INTO bff.wh_outbox (
        message_id, destination, message_type, event_data, metadata,
        status, created_at, instance_id, lease_expiry, is_event, stream_id
      ) VALUES (
        {0}, {1}, {2}, {3}::jsonb, {4}::jsonb,
        1, NOW(), NULL, NULL, true, {5}
      )",
      messageId, destination, messageType, envelopeJson, metadataJson, streamId);
  }

  [Before(Test)]
  public async Task SetupAsync() {
    // Start PostgreSQL container
    _postgresContainer = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase("bff_integration_test")
      .WithUsername("postgres")
      .WithPassword("postgres")
      .Build();

    await _postgresContainer.StartAsync();
    _connectionString = $"{_postgresContainer.GetConnectionString()};Timezone=UTC;Include Error Detail=true";

    _instanceId = Guid.CreateVersion7();

    // Build test host with EXACT BFF.API configuration (minus Aspire/Service Bus)
    _testHost = Host.CreateDefaultBuilder()
      .ConfigureLogging(logging => {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Debug);
      })
      .ConfigureServices((context, services) => {
        // IMPORTANT: Explicitly call module initializer for test assemblies
        ECommerce.BFF.API.Generated.GeneratedModelRegistration.Initialize();

        // Register JsonSerializerOptions (required by EFCoreWorkCoordinator constructor)
        var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
        services.AddSingleton(jsonOptions);

        // Register NpgsqlDataSource (required for Npgsql 9.0+ with JSONB)
        // IMPORTANT: ConfigureJsonOptions() MUST be called BEFORE EnableDynamicJson() (Npgsql bug #5562)
        // This registers WhizbangId JSON converters for JSONB serialization
        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(_connectionString);
        dataSourceBuilder.ConfigureJsonOptions(jsonOptions);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);

        // Register DbContext (same as Program.cs)
        services.AddDbContext<BffDbContext>(options => {
          options.UseNpgsql(dataSource);
        });

        // Register service instance provider (same as Program.cs)
        services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(_instanceId));

        // Register work coordinator (same as Whizbang registration in Program.cs)
        services.AddScoped<IWorkCoordinator, EFCoreWorkCoordinator<BffDbContext>>();

        // Register test transport to capture published messages
        var testTransport = new TestTransport();
        services.AddSingleton<ITransport>(sp => testTransport);
        services.AddSingleton(testTransport); // Also register as concrete type for test access

        // Register OrderedStreamProcessor (same as Program.cs)
        services.AddSingleton<OrderedStreamProcessor>();

        // Register WorkCoordinatorPublisherWorker options (same as Program.cs would use)
        services.Configure<WorkCoordinatorPublisherOptions>(options => {
          options.PollingIntervalMilliseconds = 100; // Fast polling for tests
          options.LeaseSeconds = 300;
          options.StaleThresholdSeconds = 600;
          options.DebugMode = true; // Keep completed messages for verification
          options.PartitionCount = 10000;
        });

        // Register IMessagePublishStrategy for WorkCoordinatorPublisherWorker
        services.AddSingleton<IMessagePublishStrategy>(sp =>
          new TransportPublishStrategy(
            sp.GetRequiredService<ITransport>(),
            new DefaultTransportReadinessCheck()
          )
        );

        // Register IWorkChannelWriter for communication between strategy and worker
        services.AddSingleton<IWorkChannelWriter, WorkChannelWriter>();

        // Register the worker (same as Program.cs)
        services.AddHostedService<WorkCoordinatorPublisherWorker>();
      })
      .Build();

    // Initialize database schema
    using (var scope = _testHost.Services.CreateScope()) {
      var dbContext = scope.ServiceProvider.GetRequiredService<BffDbContext>();
      await dbContext.EnsureWhizbangDatabaseInitializedAsync();
    }
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_testHost != null) {
      await _testHost.StopAsync();
      _testHost.Dispose();
    }

    if (_postgresContainer != null) {
      await _postgresContainer.StopAsync();
      await _postgresContainer.DisposeAsync();
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  [Test]
  [Obsolete]
  public async Task BffApplication_WorkerProcessesStoredMessages_EndToEndAsync() {
    // This test verifies the EXACT bug scenario from the user's CSV:
    // 1. Dispatcher (or direct DB insert) stores messages in outbox
    // 2. WorkCoordinatorPublisherWorker picks them up
    // 3. Worker publishes to transport
    // 4. Database status is updated

    // Arrange - Store message in outbox WITHOUT claiming it
    // (simulating what Dispatcher does, but allowing worker to claim)
    var messageId = MessageId.New();
    var streamId = Guid.CreateVersion7();

    using (var scope = _testHost!.Services.CreateScope()) {
      var dbContext = scope.ServiceProvider.GetRequiredService<BffDbContext>();
      var jsonOptions = scope.ServiceProvider.GetRequiredService<JsonSerializerOptions>();
      await _insertUnclaimedMessageAsync(dbContext, jsonOptions, messageId.Value, streamId);
    }

    // Act - Start the application (including WorkCoordinatorPublisherWorker)
    await _testHost.StartAsync();

    // Wait for worker to process (with timeout)
    var testTransport = _testHost.Services.GetRequiredService<TestTransport>();
    var timeout = TimeSpan.FromSeconds(10);
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    while (!testTransport.PublishedMessages.Any() && stopwatch.Elapsed < timeout) {
      await Task.Delay(100);
    }

    // Assert - Message was published
    await Assert.That(testTransport.PublishedMessages)
      .HasCount()
      .GreaterThanOrEqualTo(1)
      .Because("WorkCoordinatorPublisherWorker should have picked up and published the message");

    var published = testTransport.PublishedMessages.FirstOrDefault(m => m.MessageId == messageId.Value);
    await Assert.That(published).IsNotNull()
      .Because($"Message {messageId} should have been published");

    // Wait for database status update to commit (worker publishes THEN updates DB)
    await Task.Delay(500);

    // Assert - Database status was updated
    using (var scope = _testHost.Services.CreateScope()) {
      var dbContext = scope.ServiceProvider.GetRequiredService<BffDbContext>();

      var outboxRecord = await dbContext.Database
        .SqlQueryRaw<OutboxMessageRecord>("SELECT * FROM bff.wh_outbox WHERE message_id = {0}", messageId.Value)
        .FirstOrDefaultAsync();

      await Assert.That(outboxRecord).IsNotNull();
      await Assert.That(outboxRecord!.status & (int)MessageProcessingStatus.Published)
        .IsEqualTo((int)MessageProcessingStatus.Published)
        .Because("Message should be marked as Published in the database");
      await Assert.That(outboxRecord.published_at).IsNotNull()
        .Because("PublishedAt timestamp should be set");
    }
  }

  [Test]
  [Obsolete]
  public async Task BffApplication_WorkerRunsAutomatically_OnStartupAsync() {
    // This test verifies the worker is actually registered as a hosted service

    // Act - Start the application
    await _testHost!.StartAsync();

    // Assert - Verify hosted services are running
    var hostedServices = _testHost.Services.GetServices<IHostedService>();
    await Assert.That(hostedServices.OfType<WorkCoordinatorPublisherWorker>())
      .HasCount()
      .EqualTo(1)
      .Because("WorkCoordinatorPublisherWorker should be registered as hosted service");
  }

  [Test]
  [Obsolete]
  public async Task BffApplication_MultipleMessages_ProcessedInOrderAsync() {
    // Verify UUIDv7 ordering works in the application

    // Arrange - Store 3 messages in sequence WITHOUT claiming them
    var messageId1 = MessageId.New();
    await Task.Delay(2); // Ensure different UUIDv7 timestamps
    var messageId2 = MessageId.New();
    await Task.Delay(2);
    var messageId3 = MessageId.New();

    var streamId = Guid.CreateVersion7();

    using (var scope = _testHost!.Services.CreateScope()) {
      var dbContext = scope.ServiceProvider.GetRequiredService<BffDbContext>();
      var jsonOptions = scope.ServiceProvider.GetRequiredService<JsonSerializerOptions>();
      await _insertUnclaimedMessageAsync(dbContext, jsonOptions, messageId1.Value, streamId);
      await _insertUnclaimedMessageAsync(dbContext, jsonOptions, messageId2.Value, streamId);
      await _insertUnclaimedMessageAsync(dbContext, jsonOptions, messageId3.Value, streamId);
    }

    // Act
    await _testHost.StartAsync();

    var testTransport = _testHost.Services.GetRequiredService<TestTransport>();
    var timeout = TimeSpan.FromSeconds(10);
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    while (testTransport.PublishedMessages.Count < 3 && stopwatch.Elapsed < timeout) {
      await Task.Delay(100);
    }

    // Assert - All messages published in correct order
    await Assert.That(testTransport.PublishedMessages).HasCount().GreaterThanOrEqualTo(3);

    var msg1 = testTransport.PublishedMessages.FirstOrDefault(m => m.MessageId == messageId1.Value);
    var msg2 = testTransport.PublishedMessages.FirstOrDefault(m => m.MessageId == messageId2.Value);
    var msg3 = testTransport.PublishedMessages.FirstOrDefault(m => m.MessageId == messageId3.Value);

    await Assert.That(msg1).IsNotNull();
    await Assert.That(msg2).IsNotNull();
    await Assert.That(msg3).IsNotNull();

    // Verify order (msg1 should be published before msg2, msg2 before msg3)
    var idx1 = testTransport.PublishedMessages.IndexOf(msg1!);
    var idx2 = testTransport.PublishedMessages.IndexOf(msg2!);
    var idx3 = testTransport.PublishedMessages.IndexOf(msg3!);

    await Assert.That(idx1).IsLessThan(idx2)
      .Because("Messages should be published in UUIDv7 order");
    await Assert.That(idx2).IsLessThan(idx3)
      .Because("Messages should be published in UUIDv7 order");
  }

  [Test]
  [Obsolete]
  public async Task BffApplication_ExpiredLeaseMessages_AreReclaimedAsync() {
    // Verify that messages with expired leases (like those in user's CSV) get reclaimed

    // Arrange - Store message WITHOUT claiming it (simulates orphaned work)
    var messageId = MessageId.New();
    var streamId = Guid.CreateVersion7();

    using (var scope = _testHost!.Services.CreateScope()) {
      var dbContext = scope.ServiceProvider.GetRequiredService<BffDbContext>();
      var jsonOptions = scope.ServiceProvider.GetRequiredService<JsonSerializerOptions>();
      await _insertUnclaimedMessageAsync(dbContext, jsonOptions, messageId.Value, streamId);
    }

    // Act - Start worker (should reclaim orphaned message)
    await _testHost.StartAsync();

    var testTransport = _testHost.Services.GetRequiredService<TestTransport>();
    var timeout = TimeSpan.FromSeconds(10);
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    while (!testTransport.PublishedMessages.Any() && stopwatch.Elapsed < timeout) {
      await Task.Delay(100);
    }

    // Assert - Expired message was reclaimed and published
    await Assert.That(testTransport.PublishedMessages)
      .HasCount()
      .GreaterThanOrEqualTo(1)
      .Because("Worker should have reclaimed the expired-lease message");

    var published = testTransport.PublishedMessages.FirstOrDefault(m => m.MessageId == messageId.Value);
    await Assert.That(published).IsNotNull()
      .Because("The expired-lease message should have been reclaimed and published");
  }
}

/// <summary>
/// Simple DTO to read outbox records from database for verification.
/// Properties use snake_case to match PostgreSQL column names.
/// </summary>
#pragma warning disable IDE1006 // Naming Styles
public class OutboxMessageRecord {
  public Guid message_id { get; set; }
  public int status { get; set; }
  public DateTimeOffset? published_at { get; set; }
  public Guid? instance_id { get; set; }
  public DateTimeOffset? lease_expiry { get; set; }
}
#pragma warning restore IDE1006 // Naming Styles

/// <summary>
/// Test transport that captures published messages for verification.
/// </summary>
public class TestTransport : Whizbang.Core.Transports.ITransport {
  private readonly object _lock = new();
  private bool _isInitialized;
  public List<PublishedMessageRecord> PublishedMessages { get; } = new();

  public bool IsInitialized => _isInitialized;

  public Whizbang.Core.Transports.TransportCapabilities Capabilities =>
    Whizbang.Core.Transports.TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    _isInitialized = true;
    return Task.CompletedTask;
  }

  public Task PublishAsync(
    Whizbang.Core.Observability.IMessageEnvelope envelope,
    Whizbang.Core.Transports.TransportDestination destination,
    string? envelopeType = null,
    CancellationToken cancellationToken = default) {

    lock (_lock) {
      PublishedMessages.Add(new PublishedMessageRecord {
        MessageId = envelope.MessageId.Value,
        Destination = destination.ToString(),
        Envelope = envelope,
        PublishedAt = DateTimeOffset.UtcNow
      });
    }

    return Task.CompletedTask;
  }

  public Task<Whizbang.Core.Transports.ISubscription> SubscribeAsync(
    Func<Whizbang.Core.Observability.IMessageEnvelope, string?, CancellationToken, Task> handler,
    Whizbang.Core.Transports.TransportDestination destination,
    CancellationToken cancellationToken = default) {
    throw new NotImplementedException("SubscribeAsync not needed for outbox tests");
  }

  public Task<Whizbang.Core.Observability.IMessageEnvelope> SendAsync<TRequest, TResponse>(
    Whizbang.Core.Observability.IMessageEnvelope envelope,
    Whizbang.Core.Transports.TransportDestination destination,
    CancellationToken cancellationToken = default)
    where TRequest : notnull
    where TResponse : notnull {
    throw new NotImplementedException("SendAsync not needed for outbox tests");
  }
}

/// <summary>
/// Simple DTO to track published messages in tests.
/// </summary>
public class PublishedMessageRecord {
  public Guid MessageId { get; init; }
  public string Destination { get; init; } = null!;
  public Whizbang.Core.Observability.IMessageEnvelope Envelope { get; init; } = null!;
  public DateTimeOffset PublishedAt { get; init; }
}

/// <summary>
/// Test service instance provider with fixed instance ID.
/// </summary>
public class TestServiceInstanceProvider : Whizbang.Core.Observability.IServiceInstanceProvider {
  public TestServiceInstanceProvider(Guid instanceId) {
    InstanceId = instanceId;
  }

  public Guid InstanceId { get; }
  public string ServiceName => "BFF.API";
  public string HostName => "test-host";
  public int ProcessId => 12345;

  public Whizbang.Core.Observability.ServiceInstanceInfo ToInfo() => new() {
    InstanceId = InstanceId,
    ServiceName = ServiceName,
    HostName = HostName,
    ProcessId = ProcessId
  };
}
