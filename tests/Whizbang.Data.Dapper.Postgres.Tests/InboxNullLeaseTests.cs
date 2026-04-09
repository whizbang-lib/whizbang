using System.Text.Json;
using Dapper;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Tests that store_inbox_messages inserts inbox rows with NULL instance_id
/// and NULL lease_expiry (immediately claimable by WorkCoordinatorPublisherWorker),
/// while active_streams retains proper instance ownership.
/// TDD RED: these tests fail until the SQL change is applied.
/// </summary>
[Category("Integration")]
public class InboxNullLeaseTests : PostgresTestBase {
  private DapperWorkCoordinator _sut = null!;
  private Guid _instanceId;
  private readonly Uuid7IdProvider _idProvider = new();
  private static readonly JsonSerializerOptions _jsonOptions;

  static InboxNullLeaseTests() {
    var baseOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    _jsonOptions = new JsonSerializerOptions(baseOptions) {
      TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(
        baseOptions.TypeInfoResolver!,
        TestEnvelopeJsonContext.Default
      )
    };
  }

  [Before(Test)]
  public new async Task SetupAsync() {
    await base.SetupAsync();
    _instanceId = _idProvider.NewGuid();
    await _insertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    _sut = new DapperWorkCoordinator(ConnectionString, _jsonOptions);
  }

  // ========================================
  // Inbox row: NULL instance_id + NULL lease_expiry
  // ========================================

  [Test]
  public async Task NewInboxMessage_HasNullInstanceIdAsync() {
    // Arrange
    var messageId = _idProvider.NewGuid();
    var inboxMessage = _createInboxMessage(messageId);

    // Act
    await _sut.ProcessWorkBatchAsync(_createRequest(inboxMessage));

    // Assert — inbox row should have NULL instance_id
    var instanceId = await _getInboxInstanceIdAsync(messageId);
    await Assert.That(instanceId).IsNull()
      .Because("Inbox messages should be inserted without a lease so WorkCoordinatorPublisherWorker can claim them immediately");
  }

  [Test]
  public async Task NewInboxMessage_HasNullLeaseExpiryAsync() {
    // Arrange
    var messageId = _idProvider.NewGuid();
    var inboxMessage = _createInboxMessage(messageId);

    // Act
    await _sut.ProcessWorkBatchAsync(_createRequest(inboxMessage));

    // Assert — inbox row should have NULL lease_expiry
    var leaseExpiry = await _getInboxLeaseExpiryAsync(messageId);
    await Assert.That(leaseExpiry).IsNull()
      .Because("Inbox messages should be inserted without lease_expiry for immediate claiming");
  }

  // ========================================
  // Active streams: KEEP instance ownership
  // ========================================

  [Test]
  public async Task NewInboxMessage_ActiveStreams_RetainsInstanceOwnershipAsync() {
    // Arrange
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var inboxMessage = _createInboxMessage(messageId, streamId);

    // Act
    await _sut.ProcessWorkBatchAsync(_createRequest(inboxMessage));

    // Assert — active_streams should have the instance_id (NOT NULL)
    var activeStreamInstanceId = await _getActiveStreamInstanceIdAsync(streamId);
    await Assert.That(activeStreamInstanceId).IsEqualTo(_instanceId)
      .Because("Active streams must retain instance ownership for claim_orphaned_inbox stream ownership checks");
  }

  [Test]
  public async Task NewInboxMessage_ActiveStreams_HasLeaseExpiryAsync() {
    // Arrange
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var inboxMessage = _createInboxMessage(messageId, streamId);

    // Act
    await _sut.ProcessWorkBatchAsync(_createRequest(inboxMessage));

    // Assert — active_streams should have a non-NULL lease_expiry
    var leaseExpiry = await _getActiveStreamLeaseExpiryAsync(streamId);
    await Assert.That(leaseExpiry).IsNotNull()
      .Because("Active streams must have a lease_expiry for stream ownership tracking");
  }

  // ========================================
  // Claim: publisher worker can claim NULL-lease inbox messages
  // ========================================

  [Test]
  public async Task NullLeaseInboxMessage_ClaimedByPublisherWorkerAsync() {
    // Arrange — insert a message with SkipInboxClaiming (transport consumer),
    // then the SAME instance claims it via process_work_batch (publisher worker).
    // In production, the publisher worker runs on the same instance as the transport consumer.
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var inboxMessage = _createInboxMessage(messageId, streamId);

    await _sut.ProcessWorkBatchAsync(_createRequest(inboxMessage));

    // Act — same instance calls process_work_batch without SkipInboxClaiming (publisher worker path)
    var claimResult = await _sut.ProcessWorkBatchAsync(
      new ProcessWorkBatchRequest {
        InstanceId = _instanceId,
        ServiceName = "TestService",
        HostName = "test-host",
        ProcessId = 12345,
        OutboxCompletions = [],
        OutboxFailures = [],
        InboxCompletions = [],
        InboxFailures = [],
        ReceptorCompletions = [],
        ReceptorFailures = [],
        PerspectiveCompletions = [],
        PerspectiveEventCompletions = [],
        PerspectiveFailures = [],
        NewOutboxMessages = [],
        NewInboxMessages = [],
        RenewOutboxLeaseIds = [],
        RenewInboxLeaseIds = []
      });

    // Assert — publisher worker should claim the NULL-lease message
    await Assert.That(claimResult.InboxWork).Count().IsGreaterThanOrEqualTo(1)
      .Because("Publisher worker should claim inbox messages with NULL instance_id via claim_orphaned_inbox");
  }

  // ========================================
  // Helpers
  // ========================================

  private InboxMessage _createInboxMessage(Guid messageId, Guid? streamId = null) {
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = _createTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = streamId ?? _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    };
  }

  private ProcessWorkBatchRequest _createRequest(InboxMessage inboxMessage) {
    return new ProcessWorkBatchRequest {
      InstanceId = _instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
      Flags = WorkBatchOptions.SkipInboxClaiming, // Transport consumer skips claiming — publisher worker claims separately
      OutboxCompletions = [],
      OutboxFailures = [],
      InboxCompletions = [],
      InboxFailures = [],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [],
      NewInboxMessages = [inboxMessage],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = []
    };
  }

  private static MessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private async Task _insertServiceInstanceAsync(Guid instanceId, string serviceName, string hostName, int processId) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@instanceId, @serviceName, @hostName, @processId, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()",
      new { instanceId, serviceName, hostName, processId });
  }

  private async Task<Guid?> _getInboxInstanceIdAsync(Guid messageId) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return await connection.QueryFirstOrDefaultAsync<Guid?>(
      "SELECT instance_id FROM wh_inbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<DateTimeOffset?> _getInboxLeaseExpiryAsync(Guid messageId) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return await connection.QueryFirstOrDefaultAsync<DateTimeOffset?>(
      "SELECT lease_expiry FROM wh_inbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<Guid?> _getActiveStreamInstanceIdAsync(Guid streamId) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return await connection.QueryFirstOrDefaultAsync<Guid?>(
      "SELECT assigned_instance_id FROM wh_active_streams WHERE stream_id = @streamId",
      new { streamId });
  }

  private async Task<DateTimeOffset?> _getActiveStreamLeaseExpiryAsync(Guid streamId) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return await connection.QueryFirstOrDefaultAsync<DateTimeOffset?>(
      "SELECT lease_expiry FROM wh_active_streams WHERE stream_id = @streamId",
      new { streamId });
  }
}
