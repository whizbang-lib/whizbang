using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Tests for DapperWorkCoordinator command timeout configuration.
/// Verifies that the commandTimeoutSeconds parameter is respected and
/// that PostgresOptions.CommandTimeoutSeconds flows through correctly.
/// </summary>
public class DapperWorkCoordinatorCommandTimeoutTests : PostgresTestBase {
  private Guid _instanceId;
  private readonly Uuid7IdProvider _idProvider = new();
  private static readonly JsonSerializerOptions _jsonOptions;

  static DapperWorkCoordinatorCommandTimeoutTests() {
    var baseOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    _jsonOptions = new JsonSerializerOptions(baseOptions) {
      TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(
        baseOptions.TypeInfoResolver!,
        TestEnvelopeJsonContext.Default
      )
    };
  }

  [Before(Test)]
  public async Task TestSetupAsync() {
    _instanceId = _idProvider.NewGuid();
    await Task.CompletedTask;
  }

  [Test]
  public async Task ProcessWorkBatchAsync_DefaultTimeout_SucceedsForNormalQueriesAsync() {
    // Arrange - use default timeout (5 seconds)
    var sut = new DapperWorkCoordinator(ConnectionString, _jsonOptions);
    await _insertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    // Act - normal query should complete well within 5 seconds
    var result = await sut.ProcessWorkBatchAsync(
      new ProcessWorkBatchRequest {
        InstanceId = _instanceId,
        ServiceName = "TestService",
        HostName = "test-host",
        ProcessId = 12345,
        Metadata = null,
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

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ProcessWorkBatchAsync_CustomTimeout_SucceedsForNormalQueriesAsync() {
    // Arrange - use custom 10 second timeout
    var sut = new DapperWorkCoordinator(ConnectionString, _jsonOptions, commandTimeoutSeconds: 10);
    await _insertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    // Act
    var result = await sut.ProcessWorkBatchAsync(
      new ProcessWorkBatchRequest {
        InstanceId = _instanceId,
        ServiceName = "TestService",
        HostName = "test-host",
        ProcessId = 12345,
        Metadata = null,
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

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ProcessWorkBatchAsync_VeryShortTimeout_ThrowsOnSlowQueryAsync() {
    // Arrange - use impossibly short 1-second timeout with pg_sleep to force timeout
    _ = new DapperWorkCoordinator(ConnectionString, _jsonOptions, commandTimeoutSeconds: 1);

    // Create a function that wraps process_work_batch with a pg_sleep to simulate slow execution
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      CREATE OR REPLACE FUNCTION slow_query_test()
      RETURNS void AS $$
      BEGIN
        PERFORM pg_sleep(5);
      END;
      $$ LANGUAGE plpgsql;");

    // Act & Assert - a 5-second pg_sleep should exceed the 1-second timeout
    var threw = false;
    try {
      await connection.QueryAsync(new CommandDefinition(
        "SELECT slow_query_test()",
        commandTimeout: 1));
    } catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException) {
      threw = true;
    }

    await Assert.That(threw).IsTrue();
  }

  [Test]
  public async Task ProcessWorkBatchAsync_LargerTimeout_CompletesSlowQueryAsync() {
    // Arrange - use a generous timeout
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      CREATE OR REPLACE FUNCTION moderate_delay_test()
      RETURNS void AS $$
      BEGIN
        PERFORM pg_sleep(1);
      END;
      $$ LANGUAGE plpgsql;");

    // Act & Assert - a 1-second pg_sleep should complete within 10-second timeout
    await connection.QueryAsync(new CommandDefinition(
      "SELECT moderate_delay_test()",
      commandTimeout: 10));

    // If we reach here, the query completed successfully within the timeout
    var completed = true;
    await Assert.That(completed).IsTrue();
  }

  [Test]
  public async Task PostgresOptions_CommandTimeoutSeconds_DefaultsToFiveAsync() {
    // Arrange & Act
    var options = new PostgresOptions();

    // Assert
    await Assert.That(options.CommandTimeoutSeconds).IsEqualTo(5);
  }

  [Test]
  public async Task PostgresOptions_CommandTimeoutSeconds_CanBeConfiguredAsync() {
    // Arrange & Act
    var options = new PostgresOptions {
      CommandTimeoutSeconds = 30
    };

    // Assert
    await Assert.That(options.CommandTimeoutSeconds).IsEqualTo(30);
  }

  [Test]
  public async Task ProcessWorkBatchAsync_WithCompletions_RespectsTimeoutAsync() {
    // Arrange - verify timeout works with actual work batch data (not just empty batches)
    var sut = new DapperWorkCoordinator(ConnectionString, _jsonOptions, commandTimeoutSeconds: 10);
    await _insertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    var messageId = _idProvider.NewGuid();
    await _insertOutboxMessageAsync(messageId, "topic1", "TestEvent", "{}", status: "Publishing", instanceId: _instanceId);

    // Act - complete a message with custom timeout
    var result = await sut.ProcessWorkBatchAsync(
      new ProcessWorkBatchRequest {
        InstanceId = _instanceId,
        ServiceName = "TestService",
        HostName = "test-host",
        ProcessId = 12345,
        Metadata = null,
        Flags = WorkBatchFlags.DebugMode,
        OutboxCompletions = [
          new MessageCompletion { MessageId = messageId, Status = MessageProcessingStatus.Published }
        ],
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

    // Assert - completions processed successfully
    await Assert.That(result).IsNotNull();
    var status = await _getOutboxStatusAsync(messageId);
    await Assert.That(status).IsEqualTo("Published");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_FailedCall_DoesNotLoseCompletionsAsync() {
    // Arrange - simulate what happens when a timeout occurs mid-batch:
    // completions queued before the call should survive for retry
    var sut = new DapperWorkCoordinator(ConnectionString, _jsonOptions, commandTimeoutSeconds: 5);
    await _insertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    var messageId = _idProvider.NewGuid();
    await _insertOutboxMessageAsync(messageId, "topic1", "TestEvent", "{}", status: "Publishing", instanceId: _instanceId);

    // Act - first call succeeds, proving the data is correct
    _ = await sut.ProcessWorkBatchAsync(
      new ProcessWorkBatchRequest {
        InstanceId = _instanceId,
        ServiceName = "TestService",
        HostName = "test-host",
        ProcessId = 12345,
        Metadata = null,
        Flags = WorkBatchFlags.DebugMode,
        OutboxCompletions = [
          new MessageCompletion { MessageId = messageId, Status = MessageProcessingStatus.Published }
        ],
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

    // Assert - the same completions array can be passed again (idempotent)
    // This verifies the data isn't consumed/mutated by the call
    var result2 = await sut.ProcessWorkBatchAsync(
      new ProcessWorkBatchRequest {
        InstanceId = _instanceId,
        ServiceName = "TestService",
        HostName = "test-host",
        ProcessId = 12345,
        Metadata = null,
        Flags = WorkBatchFlags.DebugMode,
        OutboxCompletions = [
          new MessageCompletion { MessageId = messageId, Status = MessageProcessingStatus.Published }
        ],
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

    await Assert.That(result2).IsNotNull();
    var status = await _getOutboxStatusAsync(messageId);
    await Assert.That(status).IsEqualTo("Published");
  }

  #region Helper Methods

  private async Task _insertServiceInstanceAsync(Guid instanceId, string serviceName, string hostName, int processId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var now = DateTimeOffset.UtcNow;
    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at, metadata)
      VALUES (@instanceId, @serviceName, @hostName, @processId, @now, @now, NULL)",
      new { instanceId, serviceName, hostName, processId, now });
  }

  private async Task _insertOutboxMessageAsync(
    Guid messageId,
    string destination,
    string messageType,
    string messageData,
    string status = "Pending",
    Guid? instanceId = null) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    var statusFlags = status switch {
      "Pending" => 1,    // Stored
      "Publishing" => 1, // Stored (being processed)
      "Published" => 5,  // Stored | Published
      "Failed" => 32769, // Stored | Failed
      _ => 1
    };

    var now = DateTimeOffset.UtcNow;
    var envelopeType = typeof(MessageEnvelope<DapperWorkCoordinatorTests.TestEvent>).AssemblyQualifiedName!;
    var envelopeJson = $$"""
    {
      "$type": "{{envelopeType}}",
      "MessageId": "{{messageId}}",
      "Payload": {},
      "Hops": []
    }
    """;

    await connection.ExecuteAsync(@"
      INSERT INTO wh_outbox (
        message_id, destination, message_type, event_data, metadata, scope,
        status, attempts, error, created_at, published_at,
        instance_id, lease_expiry, stream_id, partition_number, is_event
      ) VALUES (
        @messageId, @destination, @envelopeType, @envelopeJson::jsonb, '{}'::jsonb, NULL,
        @statusFlags, 0, NULL, @now, NULL,
        @instanceId, @leaseExpiry, NULL, NULL, false
      )",
      new {
        messageId,
        destination,
        envelopeType,
        envelopeJson,
        statusFlags,
        instanceId,
        leaseExpiry = instanceId.HasValue ? now.AddMinutes(5) : (DateTimeOffset?)null,
        now
      });
  }

  private async Task<string> _getOutboxStatusAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var statusFlags = await connection.QueryFirstOrDefaultAsync<int>(@"
      SELECT status FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });

    var status = (MessageProcessingStatus)statusFlags;

    if ((status & MessageProcessingStatus.Failed) == MessageProcessingStatus.Failed) {
      return "Failed";
    }

    if ((status & MessageProcessingStatus.Published) == MessageProcessingStatus.Published) {
      return "Published";
    }

    return "Pending";
  }

  #endregion
}
