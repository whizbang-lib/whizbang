using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;


/// <summary>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesInboxMessages_MarksAsCompletedAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedWithErrorAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedInboxMessages_ReturnsExpiredLeasesAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MixedOperations_HandlesAllCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_StoresAndReturnsImmediatelyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_StoresWithDeduplicationAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_WithStreamId_AssignsPartitionAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_WithStreamId_AssignsPartitionAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WithEventOutbox_PersistsToEventStoreAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WithEventInbox_PersistsToEventStoreAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_EventVersionConflict_HandlesOptimisticConcurrencyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MultipleEventsInStream_IncrementsVersionAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NonEvent_DoesNotPersistToEventStoreAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ConsistentHashing_SameStreamSamePartitionAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_PartitionAssignment_WithinRangeAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_LoadBalancing_DistributesAcrossInstancesAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_InstanceFailover_RedistributesPartitionsAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_StatusFlags_AccumulateCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_PartialCompletion_TracksCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WorkBatchOptions_SetCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_StaleInstances_CleanedUpAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ActiveInstances_NotCleanedAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_WithIsEventTrue_StoresIsEventFlagAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_WithIsEventFalse_StoresIsEventFlagAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_WithIsEventTrue_StoresIsEventFlagAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_WithIsEventFalse_StoresIsEventFlagAsync</tests>
/// Dapper implementation of IWorkCoordinator for lease-based work coordination.
/// Uses the PostgreSQL process_work_batch function for atomic operations.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1845:Use span-based 'string.Concat'", Justification = "Debug logging with substring truncation - span-based operations not worth complexity for diagnostic output")]
public partial class DapperWorkCoordinator(
  string connectionString,
  JsonSerializerOptions jsonOptions,
  ILogger<DapperWorkCoordinator>? logger = null,
  int commandTimeoutSeconds = 5,
  WorkCoordinatorGate? gate = null
) : IWorkCoordinator {
  private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<DapperWorkCoordinator>? _logger = logger;
  private readonly int _commandTimeoutSeconds = commandTimeoutSeconds;
  private readonly WorkCoordinatorGate? _gate = gate;

  /// <summary>
  /// Reports perspective cursor completion directly (out-of-band).
  /// Calls complete_perspective_cursor_work SQL function directly without full work batch processing.
  /// </summary>
  /// <inheritdoc />
  public async Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    await connection.ExecuteAsync("SELECT deregister_instance(@instanceId)", new { instanceId });
  }

  /// <inheritdoc />
  public async Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    return await connection.QuerySingleAsync<WorkCoordinatorStatistics>(@"
      SELECT
        (SELECT COUNT(*) FROM wh_perspective_events WHERE processed_at IS NULL) as PendingPerspectiveEvents,
        (SELECT COUNT(*) FROM wh_outbox WHERE processed_at IS NULL) as PendingOutbox,
        (SELECT COUNT(*) FROM wh_inbox WHERE processed_at IS NULL) as PendingInbox,
        (SELECT COUNT(*) FROM wh_active_streams) as ActiveStreams");
  }

  /// <summary>
  /// Stores inbox messages directly via store_inbox_messages SQL function.
  /// Bypasses the full process_work_batch pipeline for maximum inbox throughput.
  /// </summary>
  /// <inheritdoc />
  public async Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(
    int partitionCount,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    var rows = (await connection.QueryAsync<(string table_name, long rows_recomputed)>(
      "SELECT table_name, rows_recomputed FROM recompute_partition_numbers(@partitionCount)",
      new { partitionCount })).ToList();

    long get(string table) => rows.FirstOrDefault(r => r.table_name == table).rows_recomputed;
    return new PartitionRecomputeResult {
      InboxRowsRecomputed = get("wh_inbox"),
      OutboxRowsRecomputed = get("wh_outbox"),
      ActiveStreamsRowsRecomputed = get("wh_active_streams")
    };
  }

  public async Task StoreInboxMessagesAsync(
    InboxMessage[] messages,
    int partitionCount,
    CancellationToken cancellationToken = default) {
    if (messages.Length == 0) {
      return;
    }

    var json = _serializeNewInboxMessages(messages);

    await PostgresDeadlockRetry.ExecuteAsync(async () => {
      await using var connection = new NpgsqlConnection(_connectionString);
      await connection.OpenAsync(cancellationToken);

      var now = DateTimeOffset.UtcNow;
      await connection.ExecuteAsync(
        "SELECT * FROM store_inbox_messages(@messages::jsonb, NULL::uuid, NULL::timestamptz, @now, @partitionCount)",
        new {
          messages = json,
          now,
          partitionCount
        });
    }, logger: _logger, cancellationToken: cancellationToken);
  }

  public async Task StoreOutboxMessagesAsync(
    OutboxMessage[] messages,
    int partitionCount,
    CancellationToken cancellationToken = default) {
    if (messages.Length == 0) {
      return;
    }

    var json = _serializeNewOutboxMessages(messages);

    await PostgresDeadlockRetry.ExecuteAsync(async () => {
      await using var connection = new NpgsqlConnection(_connectionString);
      await connection.OpenAsync(cancellationToken);

      var now = DateTimeOffset.UtcNow;
      await connection.ExecuteAsync(
        "SELECT * FROM store_outbox_messages(@messages::jsonb, NULL::uuid, NULL::timestamptz, @now, @partitionCount)",
        new {
          messages = json,
          now,
          partitionCount
        });
    }, logger: _logger, cancellationToken: cancellationToken);
  }

  /// <inheritdoc />
  public async Task<int> CleanupCompletedStreamsAsync(
    IReadOnlyList<Guid> streamIds,
    CancellationToken cancellationToken = default) {
    if (streamIds is null || streamIds.Count == 0) {
      return 0;
    }

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    return await connection.ExecuteScalarAsync<int>(
      "SELECT cleanup_completed_streams(@streamIds)",
      new { streamIds = streamIds.ToArray() });
  }

  private string _serializeCompletions(MessageCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }

    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  private string _serializeFailures(MessageFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }

    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

  private string _serializeNewOutboxMessages(OutboxMessage[] messages) {
    if (messages.Length == 0) {
      return "[]";
    }

    var typeInfo = _jsonOptions.GetTypeInfo(typeof(OutboxMessage[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for OutboxMessage[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(messages, typeInfo);
  }

  private string _serializeNewInboxMessages(InboxMessage[] messages) {
    if (messages.Length == 0) {
      return "[]";
    }

    var typeInfo = _jsonOptions.GetTypeInfo(typeof(InboxMessage[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for InboxMessage[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(messages, typeInfo);
  }

  private string _serializeMetadata(Dictionary<string, JsonElement>? metadata) {
    if (metadata == null || metadata.Count == 0) {
      return "{}";
    }

    var typeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for Dictionary<string, JsonElement>. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(metadata, typeInfo);
  }

  private string _serializeLeaseRenewals(Guid[] messageIds) {
    if (messageIds.Length == 0) {
      return "[]";
    }

    var typeInfo = _jsonOptions.GetTypeInfo(typeof(Guid[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for Guid[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(messageIds, typeInfo);
  }

  private string _serializePerspectiveEventCompletions(PerspectiveEventCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }

    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveEventCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveEventCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  private string _serializePerspectiveCompletions(PerspectiveCursorCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }

    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCursorCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveCursorCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  private string _serializePerspectiveFailures(PerspectiveCursorFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }

    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCursorFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveCursorFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

  private string _serializeSyncInquiries(SyncInquiry[]? inquiries) {
    if (inquiries == null || inquiries.Length == 0) {
      return "[]";
    }

    var typeInfo = _jsonOptions.GetTypeInfo(typeof(SyncInquiry[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for SyncInquiry[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(inquiries, typeInfo);
  }

  public async Task ReportPerspectiveCompletionAsync(
    PerspectiveCursorCompletion completion,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var processedEventIdsJson = JsonSerializer.Serialize(
      completion.ProcessedEventIds,
      _jsonOptions.GetTypeInfo(typeof(Guid[])) ?? throw new InvalidOperationException("No JsonTypeInfo found for Guid[]"));

    await connection.ExecuteAsync(
      "SELECT complete_perspective_cursor_work(@StreamId, @PerspectiveName, @LastEventId, @ProcessedEventIds::jsonb, @Status, @Error)",
      new {
        completion.StreamId,
        completion.PerspectiveName,
        completion.LastEventId,
        ProcessedEventIds = processedEventIdsJson,
        Status = (short)completion.Status,
        Error = (string?)null
      });
  }

  /// <summary>
  /// Reports perspective cursor failure directly (out-of-band).
  /// Calls complete_perspective_cursor_work SQL function directly without full work batch processing.
  /// </summary>
  public async Task ReportPerspectiveFailureAsync(
    PerspectiveCursorFailure failure,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var processedEventIdsJson = JsonSerializer.Serialize(
      failure.ProcessedEventIds,
      _jsonOptions.GetTypeInfo(typeof(Guid[])) ?? throw new InvalidOperationException("No JsonTypeInfo found for Guid[]"));

    await connection.ExecuteAsync(
      "SELECT complete_perspective_cursor_work(@StreamId, @PerspectiveName, @LastEventId, @ProcessedEventIds::jsonb, @Status, @Error)",
      new {
        failure.StreamId,
        failure.PerspectiveName,
        failure.LastEventId,
        ProcessedEventIds = processedEventIdsJson,
        Status = (short)failure.Status,
        failure.Error
      });
  }

  /// <summary>
  /// Completes perspective events by deleting the specified work items from wh_perspective_events.
  /// Called per-stream immediately after processing (drain mode — no buffering).
  /// </summary>
  public async Task<int> CompletePerspectiveEventsAsync(
    Guid[] workItemIds,
    bool debugMode,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    return await connection.QuerySingleAsync<int>(
      "SELECT complete_perspective_events(@p_event_work_ids, @p_debug_mode)",
      new { p_event_work_ids = workItemIds, p_debug_mode = debugMode });
  }

  /// <summary>
  /// Batch-fetches events for multiple streams in a single call.
  /// Returns denormalized rows joining wh_perspective_events with wh_event_store.
  /// Only returns events leased to the requesting instance.
  /// </summary>
  public async Task<List<StreamEventData>> GetStreamEventsAsync(
    Guid instanceId,
    Guid[] streamIds,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var now = DateTimeOffset.UtcNow;
    var results = await connection.QueryAsync<StreamEventRow>(
      "SELECT * FROM get_stream_events(@p_instance_id, @p_stream_ids, @p_now)",
      new { p_instance_id = instanceId, p_stream_ids = streamIds, p_now = now });

    return [.. results.Select(r => new StreamEventData {
      StreamId = r.out_stream_id,
      EventId = r.out_event_id,
      EventType = r.out_event_type,
      EventData = r.out_event_data,
      Metadata = r.out_metadata,
      Scope = r.out_scope,
      EventWorkId = r.out_event_work_id,
      Attempts = r.out_attempts,
    })];
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
    IReadOnlyList<Guid> streamIds,
    Guid instanceId,
    int maxPerStream = 100,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    if (streamIds.Count == 0) {
      return [];
    }

    var streamArr = streamIds is Guid[] arr ? arr : [.. streamIds];
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var rows = await connection.QueryAsync<OutboxBatchRowDto>(
      "SELECT * FROM fetch_outbox_batch(@p_stream_ids, @p_instance_id, @p_max_per_stream)",
      new { p_stream_ids = streamArr, p_instance_id = instanceId, p_max_per_stream = maxPerStream });

    return [.. rows.Select(r => new OutboxBatchRow {
      MessageId = r.message_id,
      StreamId = r.stream_id,
      Destination = r.destination,
      MessageType = r.message_type,
      EnvelopeType = r.envelope_type,
      EventData = r.event_data,
      Metadata = r.metadata,
      Scope = r.scope,
      Status = r.status,
      Attempts = r.attempts,
      PartitionNumber = r.partition_number,
      IsEvent = r.is_event,
      Error = r.error,
    })];
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
    IReadOnlyList<Guid> streamIds,
    Guid instanceId,
    int maxPerStream = 100,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    if (streamIds.Count == 0) {
      return [];
    }

    var streamArr = streamIds is Guid[] arr ? arr : [.. streamIds];
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var rows = await connection.QueryAsync<InboxBatchRowDto>(
      "SELECT * FROM fetch_inbox_batch(@p_stream_ids, @p_instance_id, @p_max_per_stream)",
      new { p_stream_ids = streamArr, p_instance_id = instanceId, p_max_per_stream = maxPerStream });

    return [.. rows.Select(r => new InboxBatchRow {
      MessageId = r.message_id,
      StreamId = r.stream_id,
      HandlerName = r.handler_name,
      MessageType = r.message_type,
      EventData = r.event_data,
      Metadata = r.metadata,
      Scope = r.scope,
      Status = r.status,
      Attempts = r.attempts,
      PartitionNumber = r.partition_number,
      IsEvent = r.is_event
    })];
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<PendingPerspectiveEvent>> FetchPendingPerspectiveEventsAsync(
    Guid streamId,
    string perspectiveName,
    Guid instanceId,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(perspectiveName);

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var rows = await connection.QueryAsync<PendingPerspectiveEventDto>(
      "SELECT * FROM fetch_pending_perspective_events(@p_stream_id, @p_perspective_name, @p_instance_id)",
      new { p_stream_id = streamId, p_perspective_name = perspectiveName, p_instance_id = instanceId });

    return [.. rows.Select(r => new PendingPerspectiveEvent(r.out_event_work_id, r.out_event_id, r.out_commit_sequence))];
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<PendingPerspectiveEvent>> ClaimAndFetchPendingPerspectiveEventsAsync(
    Guid streamId,
    string perspectiveName,
    Guid instanceId,
    TimeSpan leaseDuration,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(perspectiveName);

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var now = DateTime.UtcNow;
    var leaseExpiry = now + leaseDuration;

    var rows = await connection.QueryAsync<PendingPerspectiveEventDto>(
      "SELECT * FROM claim_and_fetch_pending_perspective_events(@p_stream_id, @p_perspective_name, @p_instance_id, @p_lease_expiry, @p_now)",
      new {
        p_stream_id = streamId,
        p_perspective_name = perspectiveName,
        p_instance_id = instanceId,
        p_lease_expiry = leaseExpiry,
        p_now = now,
      });

    return [.. rows.Select(r => new PendingPerspectiveEvent(r.out_event_work_id, r.out_event_id, r.out_commit_sequence))];
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<StreamEventData>> FetchEventsByIdsAsync(
    IReadOnlyList<Guid> eventIds,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventIds);
    if (eventIds.Count == 0) {
      return [];
    }

    var idArr = eventIds is Guid[] arr ? arr : [.. eventIds];
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var rows = await connection.QueryAsync<EventBodyRowDto>(
      "SELECT * FROM fetch_events_by_ids(@p_event_ids)",
      new { p_event_ids = idArr });

    return [.. rows.Select(r => new StreamEventData {
      StreamId = r.out_stream_id,
      EventId = r.out_event_id,
      EventType = r.out_event_type,
      EventData = r.out_event_data,
      Metadata = r.out_metadata,
      Scope = r.out_scope,
      EventWorkId = Guid.Empty
    })];
  }

#pragma warning disable CA1707, IDE1006, S1144 // Dapper DTO with snake_case to match SQL function output columns.
  private sealed class OutboxBatchRowDto {
    public Guid message_id { get; set; }
    public Guid? stream_id { get; set; }
    public string? destination { get; set; }
    public string message_type { get; set; } = string.Empty;
    public string? envelope_type { get; set; }
    public string event_data { get; set; } = string.Empty;
    public string metadata { get; set; } = string.Empty;
    public string? scope { get; set; }
    public int status { get; set; }
    public int attempts { get; set; }
    public int? partition_number { get; set; }
    public bool is_event { get; set; }
    // Slice 1 of release/v0.648.0-alpha.1 — see OutboxBatchRow.Error.
    public string? error { get; set; }
  }

  private sealed class InboxBatchRowDto {
    public Guid message_id { get; set; }
    public Guid? stream_id { get; set; }
    public string handler_name { get; set; } = string.Empty;
    public string message_type { get; set; } = string.Empty;
    public string event_data { get; set; } = string.Empty;
    public string metadata { get; set; } = string.Empty;
    public string? scope { get; set; }
    public int status { get; set; }
    public int attempts { get; set; }
    public int? partition_number { get; set; }
    public bool is_event { get; set; }
  }

  private sealed class PendingPerspectiveEventDto {
    public Guid out_event_work_id { get; set; }
    public Guid out_event_id { get; set; }
    // Dapper hydrates this from the LEFT-JOINed wh_event_store.commit_sequence column at
    // runtime via name-based mapping; Sonar's S3459 can't see that and flags it as
    // "unassigned auto-property". Suppressing the false positive here.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3459:Unassigned members should be removed",
      Justification = "Hydrated by Dapper at runtime via column name mapping; not visible to static analysis.")]
    public long? out_commit_sequence { get; set; }
  }

  private sealed class EventBodyRowDto {
    public Guid out_stream_id { get; set; }
    public Guid out_event_id { get; set; }
    public string out_event_type { get; set; } = string.Empty;
    public string out_event_data { get; set; } = string.Empty;
    public string? out_metadata { get; set; }
    public string? out_scope { get; set; }
  }
#pragma warning restore CA1707, IDE1006, S1144

  /// <summary>
  /// Gets the current checkpoint for a perspective stream.
  /// Returns null if no checkpoint exists yet.
  /// </summary>
  public async Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
    Guid streamId,
    string perspectiveName,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var result = await connection.QueryFirstOrDefaultAsync<CursorQueryResult>(
      "SELECT stream_id, perspective_name, last_event_id, status, rewind_trigger_event_id FROM wh_perspective_cursors WHERE stream_id = @StreamId AND perspective_name = @PerspectiveName",
      new { StreamId = streamId, PerspectiveName = perspectiveName });

    if (result == null) {
      return null;
    }

    return new PerspectiveCursorInfo {
      StreamId = result.stream_id,
      PerspectiveName = result.perspective_name,
      LastEventId = result.last_event_id,
      Status = (PerspectiveProcessingStatus)result.status,
      RewindTriggerEventId = result.rewind_trigger_event_id
    };
  }

  /// <summary>
  /// Extracts the message type name from an envelope type name.
  /// Example: "MessageEnvelope`1[[MyApp.ProductCreatedEvent, MyApp]], Whizbang.Core"
  /// Returns: "MyApp.ProductCreatedEvent, MyApp"
  /// </summary>
  private static string _extractMessageTypeFromEnvelopeType(string envelopeTypeName) {
    var startIndex = envelopeTypeName.IndexOf("[[", StringComparison.Ordinal);
    var endIndex = envelopeTypeName.IndexOf("]]", StringComparison.Ordinal);

    if (startIndex == -1 || endIndex == -1 || startIndex >= endIndex) {
      throw new InvalidOperationException(
        $"Invalid envelope type name format: '{envelopeTypeName}'. " +
        "Expected format: 'MessageEnvelope`1[[MessageType, Assembly]], EnvelopeAssembly'");
    }

    var messageTypeName = envelopeTypeName.Substring(startIndex + 2, endIndex - startIndex - 2);

    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new InvalidOperationException(
        $"Failed to extract message type name from envelope type: '{envelopeTypeName}'");
    }

    return messageTypeName;
  }

  /// <summary>
  /// Handles PostgreSQL RAISE DEBUG messages by logging them at Debug level.
  /// Notices are only generated when WorkBatchOptions.DebugMode is set in the SQL function.
  /// </summary>
  private void _onNotice(object? sender, NpgsqlNoticeEventArgs args) {
    if (_logger is not null) {
      LogPostgresNotice(_logger, args.Notice.Severity, args.Notice.MessageText);
    }
  }

  /// <summary>
  /// Serializes all work batch data into JSON format for database function call.
  /// </summary>
  private SerializedWorkBatchData _serializeWorkBatchData(ProcessWorkBatchRequest request) {
    return new SerializedWorkBatchData(
      OutboxCompletions: _serializeCompletions(request.OutboxCompletions),
      OutboxFailures: _serializeFailures(request.OutboxFailures),
      InboxCompletions: _serializeCompletions(request.InboxCompletions),
      InboxFailures: _serializeFailures(request.InboxFailures),
      PerspectiveEventCompletions: _serializePerspectiveEventCompletions(request.PerspectiveEventCompletions),
      PerspectiveCompletions: _serializePerspectiveCompletions(request.PerspectiveCompletions),
      PerspectiveFailures: _serializePerspectiveFailures(request.PerspectiveFailures),
      NewOutboxMessages: _serializeNewOutboxMessages(request.NewOutboxMessages),
      NewInboxMessages: _serializeNewInboxMessages(request.NewInboxMessages),
      Metadata: _serializeMetadata(request.Metadata),
      RenewOutboxLeaseIds: _serializeLeaseRenewals(request.RenewOutboxLeaseIds),
      RenewInboxLeaseIds: _serializeLeaseRenewals(request.RenewInboxLeaseIds),
      SyncInquiries: _serializeSyncInquiries(request.PerspectiveSyncInquiries)
    );
  }

  /// <summary>
  /// Parses pending event IDs from the JSON array encoded in message_data column.
  /// </summary>
  private Guid[]? _parsePendingEventIds(string? messageData) {
    if (string.IsNullOrWhiteSpace(messageData)) {
      return null;
    }

    try {
      var typeInfo = _jsonOptions.GetTypeInfo(typeof(Guid[]))
        ?? throw new InvalidOperationException("No JsonTypeInfo found for Guid[]. Ensure the type is registered in InfrastructureJsonContext.");
      var ids = JsonSerializer.Deserialize(messageData, typeInfo) as Guid[];
      return ids;
    } catch {
      return null;
    }
  }

  /// <summary>
  /// Parses processed event IDs from the metadata JSON object.
  /// SQL returns: {"processed_event_ids": [...]}
  /// </summary>
  private static Guid[]? _parseProcessedEventIds(string? metadata) {
    if (string.IsNullOrWhiteSpace(metadata)) {
      return null;
    }

    try {
      using var doc = JsonDocument.Parse(metadata);
      if (!doc.RootElement.TryGetProperty("processed_event_ids", out var idsElement)) {
        return null;
      }

      var ids = new List<Guid>();
      foreach (var element in idsElement.EnumerateArray()) {
        if (element.TryGetGuid(out var id)) {
          ids.Add(id);
        }
      }
      return ids.Count > 0 ? [.. ids] : [];
    } catch {
      return null;
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT * FROM public.perform_maintenance()";
    command.CommandTimeout = 30;

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var results = new List<MaintenanceResult>();
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new MaintenanceResult(
        reader.GetString(0),
        reader.GetInt64(1),
        reader.GetDouble(2),
        reader.GetString(3)
      ));
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<PurgedOrphanInboxRow>> PurgeOrphanInboxAsync(
      IReadOnlyList<string> handledTypeNames,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(handledTypeNames);

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT * FROM public.purge_orphan_inbox(@handled_types)";
    command.CommandTimeout = 30;
    var param = (NpgsqlParameter)command.CreateParameter();
    param.ParameterName = "handled_types";
    param.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text;
    param.Value = handledTypeNames is string[] arr ? arr : System.Linq.Enumerable.ToArray(handledTypeNames);
    command.Parameters.Add(param);

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var rows = new List<PurgedOrphanInboxRow>();
    while (await reader.ReadAsync(cancellationToken)) {
      rows.Add(new PurgedOrphanInboxRow(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2)
      ));
    }
    return rows;
  }

  #region LoggerMessage Declarations

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Processing work batch for instance {InstanceId} ({ServiceName}@{HostName}:{ProcessId}): {OutboxCompletions} outbox completions, {OutboxFailures} outbox failures, {InboxCompletions} inbox completions, {InboxFailures} inbox failures, {NewOutbox} new outbox, {NewInbox} new inbox, Flags={Flags}"
  )]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "LoggerMessage requires flat parameters matching the structured log template")]
  static partial void LogProcessingWorkBatch(ILogger logger, Guid instanceId, string serviceName, string hostName, int processId,
    int outboxCompletions, int outboxFailures, int inboxCompletions, int inboxFailures, int newOutbox, int newInbox, WorkBatchOptions flags);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Work batch processed: {OutboxWork} outbox work, {InboxWork} inbox work, {PerspectiveWork} perspective work, {SyncResults} sync results"
  )]
  static partial void LogWorkBatchProcessed(ILogger logger, int outboxWork, int inboxWork, int perspectiveWork, int syncResults);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Serializing outbox message: MessageId={MessageId}, Destination={Destination}, EnvelopeType={EnvelopeType}, HopsCount={HopsCount}"
  )]
  static partial void LogSerializingOutboxMessage(ILogger logger, Guid messageId, string? destination, string envelopeType, int hopsCount);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "First outbox message JSON: {Json}"
  )]
  static partial void LogOutboxMessageJson(ILogger logger, string json);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Deserializing envelope: Type={EnvelopeType}, JSON={EnvelopeJson}"
  )]
  static partial void LogDeserializingEnvelope(ILogger logger, string envelopeType, string envelopeJson);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Deserialized envelope: MessageId={MessageId}, Hops={HopsCount}"
  )]
  static partial void LogDeserializedEnvelope(ILogger logger, Guid messageId, int hopsCount);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "PostgreSQL Notice [{Severity}]: {Message}"
  )]
  static partial void LogPostgresNotice(ILogger logger, string severity, string message);

  [LoggerMessage(
    Level = LogLevel.Error,
    Message = "process_work_batch timed out after {TimeoutSeconds}s for instance {InstanceId} ({ServiceName})"
  )]
  static partial void LogWorkBatchTimedOut(ILogger logger, int timeoutSeconds, Guid instanceId, string serviceName, Exception ex);

  [LoggerMessage(
    Level = LogLevel.Information,
    Message = "process_work_batch cancelled for instance {InstanceId} ({ServiceName})"
  )]
  static partial void LogWorkBatchCancelled(ILogger logger, Guid instanceId, string serviceName, Exception ex);

  [LoggerMessage(
    Level = LogLevel.Error,
    Message = "process_work_batch failed for instance {InstanceId} ({ServiceName})"
  )]
  static partial void LogWorkBatchFailed(ILogger logger, Guid instanceId, string serviceName, Exception ex);

  #endregion

  #region Phase B: focused IWorkCoordinator methods

  /// <inheritdoc />
  public async Task RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var metadataJson = request.Metadata is { } meta ? meta.GetRawText() : "{}";
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    await connection.ExecuteAsync(
      "SELECT record_heartbeat(@InstanceId, @ServiceName, @HostName, @ProcessId, @Metadata::jsonb)",
      new { request.InstanceId, request.ServiceName, request.HostName, request.ProcessId, Metadata = metadataJson });
  }

  /// <inheritdoc />
  public async Task<int> NotifyScheduledRetryDueAsync(CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    return await connection.ExecuteScalarAsync<int>(
      "SELECT COALESCE(SUM(stream_count), 0)::int FROM notify_scheduled_retry_due()");
  }

  /// <inheritdoc />
  public async Task<int> CompleteOutboxPublishedAsync(IReadOnlyList<Guid> ids, bool debugMode, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(ids);
    if (ids.Count == 0) {
      return 0;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var idArray = ids is Guid[] arr ? arr : [.. ids];
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    return await connection.ExecuteScalarAsync<int>(
      "SELECT complete_outbox_published(@Ids, @DebugMode)", new { Ids = idArray, DebugMode = debugMode });
  }

  /// <inheritdoc />
  public async Task CompletePerspectiveAsync(
    IReadOnlyList<PerspectiveCursorCompletion> cursors,
    IReadOnlyList<Guid> eventWorkIds,
    bool debugMode,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(cursors);
    ArgumentNullException.ThrowIfNull(eventWorkIds);
    if (cursors.Count == 0 && eventWorkIds.Count == 0) {
      return;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var cursorsJson = cursors.Count == 0 ? "[]" : _serializePerspectiveCompletions([.. cursors]);
    var idArray = eventWorkIds is Guid[] earr ? earr : [.. eventWorkIds];
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    await connection.ExecuteAsync(
      "SELECT complete_perspective(@Cursors::jsonb, @Ids, @DebugMode)",
      new { Cursors = cursorsJson, Ids = idArray, DebugMode = debugMode });
  }

  /// <inheritdoc />
  public async Task ReportFailuresAsync(
    WorkCategory category,
    IReadOnlyList<MessageFailure> failures,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(failures);
    if (failures.Count == 0) {
      return;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var failuresJson = _serializeFailures([.. failures]);
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    await connection.ExecuteAsync(
      "SELECT report_failures(@Category, @Failures::jsonb)",
      new { Category = category.ToSqlCategory(), Failures = failuresJson });
  }

  /// <inheritdoc />
  public async Task<int> RenewLeasesAsync(
    WorkCategory category,
    IReadOnlyList<Guid> ids,
    int leaseSeconds = 300,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(ids);
    if (ids.Count == 0) {
      return 0;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var idArray = ids is Guid[] arr ? arr : [.. ids];
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    return await connection.ExecuteScalarAsync<int>(
      "SELECT renew_leases(@Category, @Ids, @LeaseSeconds)",
      new { Category = category.ToSqlCategory(), Ids = idArray, LeaseSeconds = leaseSeconds });
  }

  /// <inheritdoc />
  public async Task CommitHandlerResultAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var payload = _buildHandlerCommitPayload(request);
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    await connection.ExecuteAsync(
      "SELECT commit_handler_result(@Payload::jsonb)", new { Payload = payload });
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<HandlerBatchResult>> CommitHandlerBatchAsync(
    IReadOnlyList<HandlerCommitRequest> requests, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(requests);
    if (requests.Count == 0) {
      return [];
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var sb = new System.Text.StringBuilder("[");
    for (var i = 0; i < requests.Count; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      sb.Append(_buildHandlerCommitPayload(requests[i]));
    }
    sb.Append(']');
    var batchJson = sb.ToString();
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    var rows = await connection.QueryAsync<HandlerBatchRow>(
      "SELECT handler_id AS HandlerId, success AS Success, error_message AS ErrorMessage FROM commit_handler_batch(@Results::jsonb)",
      new { Results = batchJson });
    return [.. rows.Select(r => new HandlerBatchResult(r.HandlerId, r.Success, r.ErrorMessage))];
  }

  /// <inheritdoc />
  public async Task FlushCompletionsAsync(FlushCompletionsRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var outboxIds = request.OutboxIds is null ? []
      : (request.OutboxIds is Guid[] a ? a : [.. request.OutboxIds]);
    var perspIds = request.PerspectiveEventWorkIds is null ? []
      : (request.PerspectiveEventWorkIds is Guid[] p ? p : [.. request.PerspectiveEventWorkIds]);
    var cursorsJson = request.PerspectiveCursors is null || request.PerspectiveCursors.Count == 0
      ? "[]" : _serializePerspectiveCompletions([.. request.PerspectiveCursors]);
    var failuresJson = _buildFailuresByCategoryJson(request.FailuresByCategory);
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    await connection.ExecuteAsync(
      "SELECT flush_completions(@Outbox, @Cursors::jsonb, @Persp, @Failures::jsonb)",
      new { Outbox = outboxIds, Cursors = cursorsJson, Persp = perspIds, Failures = failuresJson });
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<SyncInquiryResult>> ResolveSyncInquiriesAsync(
    IReadOnlyList<Whizbang.Core.Perspectives.Sync.SyncInquiry> inquiries,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(inquiries);
    if (inquiries.Count == 0) {
      return [];
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var json = _buildInquiriesJson(inquiries);
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    var rows = await connection.QueryAsync<SyncInquiryRow>(
      "SELECT inquiry_id AS InquiryId, stream_id AS StreamId, pending_count AS PendingCount, processed_count AS ProcessedCount FROM resolve_sync_inquiries(@Inq::jsonb)",
      new { Inq = json });
    return [.. rows.Select(r => new SyncInquiryResult {
      InquiryId = r.InquiryId,
      StreamId = r.StreamId,
      PendingCount = r.PendingCount,
      ProcessedCount = r.ProcessedCount
    })];
  }

  /// <inheritdoc />
  public async Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    // Phase C lands the full envelope-deserializing path. For now Dapper backend
    // returns perspective_stream rows + throws on outbox/inbox to keep callers safe.
    var rows = await connection.QueryAsync<ClaimWorkRow>(
      "SELECT source AS Source, work_id AS WorkId, work_stream_id AS StreamId FROM claim_work(@Id, @Svc, @Host, @Pid, @Max, @Part, @Lease)",
      new {
        Id = request.InstanceId,
        Svc = request.ServiceName,
        Host = request.HostName,
        Pid = request.ProcessId,
        Max = request.MaxStreams,
        Part = request.PartitionCount,
        Lease = request.LeaseSeconds
      });
    var perspectiveStreamIds = new List<Guid>();
    var sawOutboxOrInbox = false;
    foreach (var r in rows) {
      if (r.Source == "perspective_stream" && r.StreamId.HasValue) {
        perspectiveStreamIds.Add(r.StreamId.Value);
      } else if (r.Source is "outbox" or "inbox") {
        sawOutboxOrInbox = true;
      }
    }
    if (sawOutboxOrInbox) {
      throw new NotImplementedException(
        "DapperWorkCoordinator.ClaimWorkAsync: full envelope deserialization for outbox/inbox lands in Phase C polish. " +
        "Use ProcessWorkBatchAsync until then.");
    }
    return new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      PerspectiveStreamIds = perspectiveStreamIds
    };
  }

  // --- helpers shared by the new methods ---

  private string _buildHandlerCommitPayload(HandlerCommitRequest request) {
    var sb = new System.Text.StringBuilder("{");
    sb.Append("\"handler_id\":\"").Append(request.HandlerId).Append('"');
    sb.Append(",\"instance_id\":\"").Append(request.InstanceId).Append('"');
    sb.Append(",\"service_name\":\"").Append(_jsonEscape(request.ServiceName)).Append('"');
    sb.Append(",\"host_name\":\"").Append(_jsonEscape(request.HostName)).Append('"');
    sb.Append(",\"process_id\":").Append(request.ProcessId);
    sb.Append(",\"partition_count\":").Append(request.PartitionCount);
    sb.Append(",\"debug_mode\":").Append(request.DebugMode ? "true" : "false");
    sb.Append(",\"inbox_completion\":{")
      .Append("\"MessageId\":\"").Append(request.InboxCompletion.MessageId).Append("\",")
      .Append("\"Status\":").Append(request.InboxCompletion.Status)
      .Append('}');
    var newOutboxArr = request.NewOutboxMessages?.ToArray() ?? [];
    sb.Append(",\"new_outbox_messages\":").Append(_serializeNewOutboxMessages(newOutboxArr));
    var newInboxArr = request.NewInboxMessages?.ToArray() ?? [];
    sb.Append(",\"new_inbox_messages\":").Append(_serializeNewInboxMessages(newInboxArr));
    sb.Append('}');
    return sb.ToString();
  }

  private string _buildFailuresByCategoryJson(IReadOnlyList<CategoryFailures>? failures) {
    if (failures is null || failures.Count == 0) {
      return "[]";
    }
    var sb = new System.Text.StringBuilder("[");
    for (var i = 0; i < failures.Count; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      sb.Append("{\"Category\":\"").Append(failures[i].Category.ToSqlCategory()).Append("\",")
        .Append("\"Items\":").Append(_serializeFailures([.. failures[i].Items])).Append('}');
    }
    sb.Append(']');
    return sb.ToString();
  }

  private static string _buildInquiriesJson(IReadOnlyList<Whizbang.Core.Perspectives.Sync.SyncInquiry> inquiries) {
    var sb = new System.Text.StringBuilder("[");
    for (var i = 0; i < inquiries.Count; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      var inq = inquiries[i];
      sb.Append("{\"InquiryId\":\"").Append(inq.InquiryId).Append("\",")
        .Append("\"StreamId\":\"").Append(inq.StreamId).Append("\",")
        .Append("\"PerspectiveName\":\"").Append(_jsonEscape(inq.PerspectiveName)).Append("\",")
        .Append("\"DiscoverPendingFromOutbox\":").Append(inq.DiscoverPendingFromOutbox ? "true" : "false").Append(',')
        .Append("\"IncludePendingEventIds\":").Append(inq.IncludePendingEventIds ? "true" : "false").Append(',')
        .Append("\"IncludeProcessedEventIds\":").Append(inq.IncludeProcessedEventIds ? "true" : "false");
      sb.Append('}');
    }
    sb.Append(']');
    return sb.ToString();
  }

  private static string _jsonEscape(string s) =>
    s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

  private sealed record HandlerBatchRow(Guid HandlerId, bool Success, string? ErrorMessage);
  private sealed record SyncInquiryRow(Guid InquiryId, Guid StreamId, int PendingCount, int ProcessedCount);
  private sealed record ClaimWorkRow(string Source, Guid? WorkId, Guid? StreamId);

  #endregion
}

/// <summary>
/// Internal DTO for mapping process_work_batch function results.
/// Matches the function's return type structure with snake_case naming (PostgreSQL convention).
/// Updated for decomposed process_work_batch (migrations 009-029).
/// </summary>
internal class WorkBatchRow {
  public int instance_rank { get; set; }
  public int active_instance_count { get; set; }
  public required string source { get; set; }  // 'outbox', 'inbox', 'receptor', 'perspective'
  public Guid? work_id { get; set; }  // NULL for stream-only perspective rows (drain mode)
  public Guid? work_stream_id { get; set; }
  public int? partition_number { get; set; }  // Partition assignment for load balancing
  public string? destination { get; set; }  // Topic name (outbox) or handler name (inbox)
  public string? message_type { get; set; }  // Assembly qualified name or perspective name
  public string? envelope_type { get; set; }  // Assembly qualified name of envelope type (for outbox only)
  public string? message_data { get; set; }  // Complete serialized MessageEnvelope<T> as JSON
  public string? metadata { get; set; }
  public int status { get; set; }  // MessageProcessingStatus flags
  public int attempts { get; set; }
  public bool is_newly_stored { get; set; }
  public bool is_orphaned { get; set; }
  public string? perspective_name { get; set; }
}

/// <summary>
/// Internal DTO for mapping get_stream_events function results.
/// Matches the function's return type structure with snake_case naming (PostgreSQL convention).
/// </summary>
internal class StreamEventRow {
  public Guid out_stream_id { get; set; }
  public Guid out_event_id { get; set; }
  public string out_event_type { get; set; } = string.Empty;
  public string out_event_data { get; set; } = string.Empty;
  public string? out_metadata { get; set; }
  public string? out_scope { get; set; }
  public Guid out_event_work_id { get; set; }
  // v0.502 slice C.4c — wh_perspective_events.attempts surfaced by get_stream_events
  // for the perspective worker's pre-apply DLQ check. Dapper hydrates by column name;
  // S3459 false positive suppressed (same pattern as out_commit_sequence above).
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3459:Unassigned members should be removed",
    Justification = "Hydrated by Dapper at runtime via column name mapping; not visible to static analysis.")]
  public int out_attempts { get; set; }
}

/// <summary>
/// DTO for querying perspective cursor info.
/// Uses snake_case to match PostgreSQL column names.
/// </summary>
internal class CursorQueryResult {
  public Guid stream_id { get; set; }
  public string perspective_name { get; set; } = string.Empty;
  public Guid? last_event_id { get; set; }
  public short status { get; set; }
  public Guid? rewind_trigger_event_id { get; set; }
}

/// <summary>
/// Holds all serialized JSON data for the database function call.
/// Extracted from ProcessWorkBatchAsync to reduce cognitive complexity.
/// </summary>
internal sealed record SerializedWorkBatchData(
  string OutboxCompletions,
  string OutboxFailures,
  string InboxCompletions,
  string InboxFailures,
  string PerspectiveEventCompletions,
  string PerspectiveCompletions,
  string PerspectiveFailures,
  string NewOutboxMessages,
  string NewInboxMessages,
  string Metadata,
  string RenewOutboxLeaseIds,
  string RenewInboxLeaseIds,
  string SyncInquiries
);

