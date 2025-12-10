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

  private static IMessageEnvelope<object> CreateTestEnvelope(Guid messageId) {
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new TestEvent(),
      Hops = []
    };
    // Cast to IMessageEnvelope<object> - safe because MessageEnvelope<TMessage> implements IMessageEnvelope<object> via interface hierarchy
    return envelope as IMessageEnvelope<object>
      ?? throw new InvalidOperationException("Envelope must implement IMessageEnvelope<object>");
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
        // Register DbContext (same as Program.cs)
        services.AddDbContext<BffDbContext>(options => {
          options.UseNpgsql(_connectionString);
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
        services.AddSingleton(new WorkCoordinatorPublisherOptions {
          PollingIntervalMilliseconds = 100, // Fast polling for tests
          LeaseSeconds = 300,
          StaleThresholdSeconds = 600
        });

        // Register IMessagePublishStrategy for WorkCoordinatorPublisherWorker
        var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
        services.AddSingleton<IMessagePublishStrategy>(sp =>
          new TransportPublishStrategy(
            sp.GetRequiredService<ITransport>(),
            new DefaultTransportReadinessCheck()
          )
        );

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
  public async Task BffApplication_WorkerProcessesStoredMessages_EndToEndAsync() {
    // This test verifies the EXACT bug scenario from the user's CSV:
    // 1. Dispatcher (or direct DB insert) stores messages in outbox
    // 2. WorkCoordinatorPublisherWorker picks them up
    // 3. Worker publishes to transport
    // 4. Database status is updated

    // Arrange - Store message in outbox (simulating what Dispatcher does)
    var messageId = MessageId.New();
    var streamId = Guid.CreateVersion7();

    using (var scope = _testHost!.Services.CreateScope()) {
      var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

      await workCoordinator.ProcessWorkBatchAsync(
        _instanceId,
        "BFF.API",
        "test-host",
        12345,
        metadata: null,
        outboxCompletions: [],
        outboxFailures: [],
        inboxCompletions: [],
        inboxFailures: [],
        receptorCompletions: [],
        receptorFailures: [],
        perspectiveCompletions: [],
        perspectiveFailures: [],
        newOutboxMessages: [
          new OutboxMessage {
            MessageId = messageId.Value,
            Destination = "products",
            Envelope = CreateTestEnvelope(messageId.Value),
            EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
            IsEvent = true,
            StreamId = streamId,
            MessageType = "TestMessage, TestAssembly"
          }
        ],
        newInboxMessages: [],
        renewOutboxLeaseIds: [],
        renewInboxLeaseIds: []
      );
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

    // Assert - Database status was updated
    using (var scope = _testHost.Services.CreateScope()) {
      var dbContext = scope.ServiceProvider.GetRequiredService<BffDbContext>();

      var outboxRecord = await dbContext.Database
        .SqlQueryRaw<OutboxMessageRecord>("SELECT * FROM wh_outbox WHERE message_id = {0}", messageId.Value)
        .FirstOrDefaultAsync();

      await Assert.That(outboxRecord).IsNotNull();
      await Assert.That(outboxRecord!.Status & (int)MessageProcessingStatus.Published)
        .IsEqualTo((int)MessageProcessingStatus.Published)
        .Because("Message should be marked as Published in the database");
      await Assert.That(outboxRecord.PublishedAt).IsNotNull()
        .Because("PublishedAt timestamp should be set");
    }
  }

  [Test]
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
  public async Task BffApplication_MultipleMessages_ProcessedInOrderAsync() {
    // Verify UUIDv7 ordering works in the application

    // Arrange - Store 3 messages in sequence
    var messageId1 = MessageId.New();
    await Task.Delay(2); // Ensure different UUIDv7 timestamps
    var messageId2 = MessageId.New();
    await Task.Delay(2);
    var messageId3 = MessageId.New();

    var streamId = Guid.CreateVersion7();

    using (var scope = _testHost!.Services.CreateScope()) {
      var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

      await workCoordinator.ProcessWorkBatchAsync(
        _instanceId,
        "BFF.API",
        "test-host",
        12345,
        metadata: null,
        outboxCompletions: [],
        outboxFailures: [],
        inboxCompletions: [],
        inboxFailures: [],
        receptorCompletions: [],
        receptorFailures: [],
        perspectiveCompletions: [],
        perspectiveFailures: [],
        newOutboxMessages: [
          new OutboxMessage {
            MessageId = messageId1.Value,
            Destination = "products",
            Envelope = CreateTestEnvelope(messageId1.Value),
            EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
            IsEvent = true,
            StreamId = streamId,
            MessageType = "TestMessage, TestAssembly"
          },
          new OutboxMessage {
            MessageId = messageId2.Value,
            Destination = "products",
            Envelope = CreateTestEnvelope(messageId2.Value),
            EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
            IsEvent = true,
            StreamId = streamId,
            MessageType = "TestMessage, TestAssembly"
          },
          new OutboxMessage {
            MessageId = messageId3.Value,
            Destination = "products",
            Envelope = CreateTestEnvelope(messageId3.Value),
            EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
            IsEvent = true,
            StreamId = streamId,
            MessageType = "TestMessage, TestAssembly"
          }
        ],
        newInboxMessages: [],
        renewOutboxLeaseIds: [],
        renewInboxLeaseIds: []
      );
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
  public async Task BffApplication_ExpiredLeaseMessages_AreReclaimedAsync() {
    // Verify that messages with expired leases (like those in user's CSV) get reclaimed

    // Arrange - Store message with very short lease (immediately expired)
    var messageId = MessageId.New();
    var streamId = Guid.CreateVersion7();

    using (var scope = _testHost!.Services.CreateScope()) {
      var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

      await workCoordinator.ProcessWorkBatchAsync(
        _instanceId,
        "BFF.API",
        "test-host",
        12345,
        metadata: null,
        outboxCompletions: [],
        outboxFailures: [],
        inboxCompletions: [],
        inboxFailures: [],
        receptorCompletions: [],
        receptorFailures: [],
        perspectiveCompletions: [],
        perspectiveFailures: [],
        newOutboxMessages: [
          new OutboxMessage {
            MessageId = messageId.Value,
            Destination = "products",
            Envelope = CreateTestEnvelope(messageId.Value),
            EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
            IsEvent = true,
            StreamId = streamId,
            MessageType = "TestMessage, TestAssembly"
          }
        ],
        newInboxMessages: [],
        renewOutboxLeaseIds: [],
        renewInboxLeaseIds: [],
        leaseSeconds: -1  // Immediately expired!
      );
    }

    // Act - Start worker (should reclaim expired message)
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
/// </summary>
public class OutboxMessageRecord {
  public Guid MessageId { get; set; }
  public int Status { get; set; }
  public DateTimeOffset? PublishedAt { get; set; }
  public Guid? InstanceId { get; set; }
  public DateTimeOffset? LeaseExpiry { get; set; }
}

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
    Func<Whizbang.Core.Observability.IMessageEnvelope, CancellationToken, Task> handler,
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
